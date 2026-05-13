using CMS.ContentEngine;
using Kentico.Xperience.UMT.Model;
using Microsoft.EntityFrameworkCore;
using Kentico.Xperience.UMT.Services;
using MediatR;
using Microsoft.Extensions.Logging;
using Migration.Tool.Common;
using Migration.Tool.Common.Abstractions;
using Migration.Tool.Common.Helpers;
using Migration.Tool.Common.MigrationProtocol;
using Migration.Tool.KX13.Context;
using Migration.Tool.KX13.Models;
using Migration.Tool.Source.Contexts;
using Migration.Tool.Source.Mappers;

namespace Migration.Tool.Source.Handlers;

/// <summary>
/// Migrates K13 CmsTagGroup → XbyK Taxonomy
/// Migrates K13 CmsTag      → XbyK Tag (Term)
/// ต้อง run ก่อน MigratePagesCommand
/// </summary>
public class MigrateTagsCommandHandler(
    ILogger<MigrateTagsCommandHandler> logger,
    IDbContextFactory<KX13Context> kx13ContextFactory,
    IImporter importer,
    IProtocol protocol,
    PrimaryKeyMappingContext primaryKeyMappingContext,
    IUmtMapper<CmsTagMapperSource> cmsTagMapper
) : IRequestHandler<MigrateTagsCommand, CommandResult>
{
    // public: เพื่อให้ TagTaxonomyFieldMigration เข้าถึงได้
    public static readonly Dictionary<int, Guid> TagGroupToTaxonomyGuid = new();
    public static readonly Dictionary<int, Guid> TagIdToTermGuid = new();
    public static readonly Dictionary<int, string> TagIdToName = new();
    public static readonly Dictionary<int, int> TagIdToGroupId = new();
    // ClassName → TaxonomyGuid: สำหรับ system field (DocumentTags) ที่ไม่มี TagGroupID ใน settings
    public static readonly Dictionary<string, Guid> ClassNameToTaxonomyGuid = new();

    public async Task<CommandResult> Handle(
        MigrateTagsCommand request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("==== START: Migrate K13 Tags → XbyK Taxonomy ====");

        await using var kx13Context = await kx13ContextFactory.CreateDbContextAsync(cancellationToken);

        await MigrateTaxonomies(kx13Context);
        await MigrateTagTerms(kx13Context);
        await BuildClassTagGroupMapping(kx13Context);

        logger.LogInformation(
            "==== END: Migrate Tags (Taxonomies: {TaxCount}, Tags: {TagCount}) ====",
            TagGroupToTaxonomyGuid.Count,
            TagIdToTermGuid.Count);

        return new GenericCommandResult();
    }

    // ══════════════════════════════════════════════════════════════
    // STEP 3 : ClassName → TaxonomyGuid mapping (for system DocumentTags field)
    // ══════════════════════════════════════════════════════════════
    private async Task BuildClassTagGroupMapping(KX13Context kx13Context)
    {
        // หา TagGroupId ที่ใช้บ่อยที่สุดใน each page class
        var mappings = (
            from doc in kx13Context.CmsDocuments
            where doc.DocumentTagGroupId.HasValue
            join tree in kx13Context.CmsTrees on doc.DocumentNodeId equals tree.NodeId
            join cls in kx13Context.CmsClasses on tree.NodeClassId equals cls.ClassId
            group doc by new { cls.ClassName, doc.DocumentTagGroupId } into g
            select new { g.Key.ClassName, g.Key.DocumentTagGroupId, Count = g.Count() }
        ).ToList();

        var classToGroup = mappings
            .GroupBy(x => x.ClassName)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.Count).First().DocumentTagGroupId!.Value);

        foreach (var (className, tagGroupId) in classToGroup)
        {
            if (TagGroupToTaxonomyGuid.TryGetValue(tagGroupId, out var taxGuid))
            {
                ClassNameToTaxonomyGuid[className] = taxGuid;
                logger.LogInformation(
                    "Class '{Class}' → TagGroupId={GroupId} → TaxonomyGuid={Guid}",
                    className, tagGroupId, taxGuid);
            }
        }

        logger.LogInformation("Built ClassNameToTaxonomyGuid: {Count} entries", ClassNameToTaxonomyGuid.Count);
    }

    // ══════════════════════════════════════════════════════════════
    // STEP 1 : CmsTagGroup → Taxonomy
    // ══════════════════════════════════════════════════════════════
    private async Task MigrateTaxonomies(KX13Context kx13Context)
    {
        var tagGroups = kx13Context.CmsTagGroups.ToList();
        logger.LogInformation("Found {Count} Tag Groups", tagGroups.Count);

        foreach (var group in tagGroups)
        {
            protocol.FetchedSource(group);

            // Deterministic GUID — เหมือนกันทุก run (ใช้ TagGroupGuid จาก K13)
            var taxonomyGuid = GuidV5.NewNameBased(
                new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"),
                group.TagGroupGuid.ToString()
            );

            var taxonomyModel = new TaxonomyModel
            {
                TaxonomyGUID = taxonomyGuid,
                TaxonomyName = group.TagGroupName,
                TaxonomyTitle = group.TagGroupDisplayName,
                TaxonomyDescription = group.TagGroupDescription,
                TaxonomyTranslations = [],
            };

            var result = await importer.ImportAsync(taxonomyModel);

            if (!result.Success)
            {
                logger.LogError(
                    "Taxonomy '{Name}' failed: {Ex}",
                    group.TagGroupName, result.Exception?.Message);

                protocol.Append(
                    HandbookReferences
                        .ErrorCreatingTargetInstance<TaxonomyInfo>(result.Exception)
                        .WithIdentityPrint(group));
                continue;
            }

            logger.LogInformation(
                "Taxonomy created: '{Name}' GUID={Guid}",
                group.TagGroupName, taxonomyGuid);

            TagGroupToTaxonomyGuid[group.TagGroupId] = taxonomyGuid;

            if (result.Imported is TaxonomyInfo taxInfo)
            {
                primaryKeyMappingContext.SetMapping<CmsTagGroup>(
                    g => g.TagGroupId,
                    group.TagGroupId,
                    taxInfo.TaxonomyID);
            }
        }
    }

    // ══════════════════════════════════════════════════════════════
    // STEP 2 : CmsTag → Tag (Term) ใช้ CmsTagMapper
    // ══════════════════════════════════════════════════════════════
    private async Task MigrateTagTerms(KX13Context kx13Context)
    {
        var tags = kx13Context.CmsTags.ToList();
        logger.LogInformation("Found {Count} Tags", tags.Count);

        var tagId2Guid = tags.ToDictionary(
            t => t.TagId,
            t => t.TagGuid
        );

        foreach (var tag in tags)
        {
            protocol.FetchedSource(tag);

            if (!TagGroupToTaxonomyGuid.TryGetValue(tag.TagGroupId, out var taxonomyGuid))
            {
                logger.LogWarning(
                    "Skip tag '{Tag}' — Taxonomy not found for TagGroupId={GID}",
                    tag.TagName, tag.TagGroupId);
                continue;
            }

            var mapperSource = new CmsTagMapperSource(taxonomyGuid, tag, tagId2Guid);
            var umtModels = cmsTagMapper.Map(mapperSource);

            foreach (var umtModel in umtModels)
            {
                var result = await importer.ImportAsync(umtModel);

                if (!result.Success)
                {
                    logger.LogError(
                        "Tag '{Tag}' failed: {Ex}",
                        tag.TagName, result.Exception?.Message);
                    continue;
                }

                logger.LogInformation(
                    "Tag created: '{Tag}' → TaxonomyGUID={TaxGuid}",
                    tag.TagName, taxonomyGuid);

                TagIdToTermGuid[tag.TagId] = tag.TagGuid;
                TagIdToName[tag.TagId] = tag.TagName;
                TagIdToGroupId[tag.TagId] = tag.TagGroupId;

                if (result.Imported is TagInfo tagInfo)
                {
                    primaryKeyMappingContext.SetMapping<CmsTag>(
                        t => t.TagId,
                        tag.TagId,
                        tagInfo.TagID);
                }
            }
        }
    }
}
