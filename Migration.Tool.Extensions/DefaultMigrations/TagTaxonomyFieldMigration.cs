using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Migration.Tool.Common;
using Migration.Tool.Common.Enumerations;
using Migration.Tool.Common.Helpers;
using Migration.Tool.KX13.Context;
using Migration.Tool.KXP.Api.Services.CmsClass;
using Migration.Tool.Source.Contexts;
using Migration.Tool.Source.Handlers;

namespace Migration.Tool.Extensions.DefaultMigrations;

/// <summary>
/// IFieldMigration: แปลง K13 DocumentTags (longtext / TagSelector)
///                  → XbyK Taxonomy field (JSON array of TagGUIDs)
/// </summary>
public class TagTaxonomyFieldMigration(
    ILogger<TagTaxonomyFieldMigration> logger,
    IDbContextFactory<KX13Context> kx13ContextFactory
) : IFieldMigration
{
    public int Rank => 50_000;

    // ตรวจแค่ FormControl เพราะ:
    // - ตอน --page-types: context เป็น EmptySourceObjectContext → ต้องผ่านเพื่อแปลง field definition
    // - ตอน --pages: context เป็น DocumentSourceObjectContext → ต้องผ่านเพื่อแปลง field value
    public bool ShallMigrate(FieldMigrationContext context) =>
        context.SourceFormControl != null &&
        context.SourceFormControl.Equals(
            Kx13FormControls.UserControlForText.TagSelector,
            StringComparison.OrdinalIgnoreCase
        );

    public async Task<FieldMigrationResult> MigrateValue(
        object? sourceValue,
        FieldMigrationContext context)
    {
        if (sourceValue is not string rawTags || string.IsNullOrWhiteSpace(rawTags))
        {
            return new FieldMigrationResult(true, null);
        }

        var tagNames = rawTags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // ดึง DocumentTagGroupId ผ่าน DocumentId จาก context
        int? documentTagGroupId = null;
        if (context.SourceObjectContext is DocumentSourceObjectContext docCtx)
        {
            await using var kx13Context = await kx13ContextFactory.CreateDbContextAsync();
            var doc = kx13Context.CmsDocuments.FirstOrDefault(d => d.DocumentId == docCtx.DocumentId);
            documentTagGroupId = doc?.DocumentTagGroupId;
        }

        var termGuids = new List<string>();

        foreach (var tagName in tagNames)
        {
            var guid = FindTermGuid(tagName, documentTagGroupId);
            if (guid.HasValue)
            {
                termGuids.Add(guid.Value.ToString());
                logger.LogTrace("Mapped '{Tag}' → {Guid}", tagName, guid.Value);
            }
            else
            {
                logger.LogWarning("Tag '{Tag}' not found in migration map — skipped", tagName);
            }
        }

        if (termGuids.Count == 0)
        {
            logger.LogWarning("No tags mapped for value '{Value}'", rawTags);
            return new FieldMigrationResult(false, null);
        }

        var json = System.Text.Json.JsonSerializer.Serialize(termGuids);
        return new FieldMigrationResult(true, json);
    }

    public void MigrateFieldDefinition(
        FormDefinitionPatcher formDefinitionPatcher,
        XElement field,
        XAttribute? columnTypeAttr,
        string fieldDescriptor)
    {
        logger.LogInformation("MigrateFieldDefinition (TagTaxonomy): '{Field}'", fieldDescriptor);

        columnTypeAttr?.SetValue("taxonomy");

        // ลบ system="true" เพื่อให้ field แสดงใน Content Types > Fields admin UI
        field.Attribute("system")?.Remove();

        var settings = field.EnsureElement(FormDefinitionPatcher.FieldElemSettings);

        settings.EnsureElement(
            FormDefinitionPatcher.SettingsElemControlname,
            e => e.Value = "Kentico.Administration.TagSelector"
        );

        // Resolve TaxonomyGUID (4 ways, in order of preference):
        //   1a. TagGroupID in field XML → in-memory dict (fast, when --tags ran first)
        //   1b. TagGroupID in field XML → query K13 directly (when dict is empty)
        //   2.  ClassNameToTaxonomyGuid in-memory dict (populated during --tags in same process)
        //   3.  Most-common DocumentTagGroupId per class → K13 DB query
        Guid? taxonomyGuid = null;

        var tagGroupIdElem = settings.Element("TagGroupID");
        if (tagGroupIdElem != null && int.TryParse(tagGroupIdElem.Value, out var tagGroupIdFromXml))
        {
            // 1a: try in-memory dict first
            if (MigrateTagsCommandHandler.TagGroupToTaxonomyGuid.TryGetValue(tagGroupIdFromXml, out var guidFromDict))
            {
                taxonomyGuid = guidFromDict;
                logger.LogInformation("TaxonomyGUID resolved from field TagGroupID (dict) for '{Field}'", fieldDescriptor);
            }
            else
            {
                // 1b: dict empty (--tags not in same process) → compute GuidV5 from K13 directly
                taxonomyGuid = ResolveFromTagGroupId(tagGroupIdFromXml);
                if (taxonomyGuid.HasValue)
                {
                    logger.LogInformation("TaxonomyGUID resolved from field TagGroupID (K13) for '{Field}': {Guid}", fieldDescriptor, taxonomyGuid.Value);
                }
            }
        }

        if (!taxonomyGuid.HasValue && formDefinitionPatcher.CurrentClassName != null &&
            MigrateTagsCommandHandler.ClassNameToTaxonomyGuid.TryGetValue(
                formDefinitionPatcher.CurrentClassName, out var guidFromClass))
        {
            taxonomyGuid = guidFromClass;
            logger.LogInformation("TaxonomyGUID resolved from in-memory map for class '{Class}'", formDefinitionPatcher.CurrentClassName);
        }

        if (!taxonomyGuid.HasValue && formDefinitionPatcher.CurrentClassName != null)
        {
            // Fallback: query K13 documents for most-common TagGroupId for this class
            taxonomyGuid = ResolveFromK13(formDefinitionPatcher.CurrentClassName);
            if (taxonomyGuid.HasValue)
            {
                logger.LogInformation(
                    "TaxonomyGUID resolved from K13 DB for class '{Class}': {Guid}",
                    formDefinitionPatcher.CurrentClassName, taxonomyGuid.Value);
            }
        }

        if (taxonomyGuid.HasValue)
        {
            // XbyK TagSelectorComponent expects "TaxonomyGroup" as JSON array: ["guid"]
            // NOT "TaxonomyGUID" — found from MigrateCategoriesCommandHandler source
            settings.EnsureElement("TaxonomyGroup", e => e.Value = $"[\"{taxonomyGuid.Value:D}\"]");
            settings.Element("TaxonomyGUID")?.Remove(); // remove if set incorrectly in previous run
            logger.LogInformation(
                "Set TaxonomyGroup=[\"{Guid}\"] on field '{Field}' (class '{Class}')",
                taxonomyGuid.Value, fieldDescriptor, formDefinitionPatcher.CurrentClassName);
        }
        else
        {
            logger.LogWarning(
                "Cannot resolve TaxonomyGUID for field '{Field}' (class '{Class}') — " +
                "run --tags before --page-types, or ensure DocumentTagGroupId is set in K13",
                fieldDescriptor, formDefinitionPatcher.CurrentClassName);
        }

        // ลบ K13 settings เก่า
        settings.Element("TagGroupID")?.Remove();
        settings.Element("taggroup")?.Remove();
        settings.Element("TagGroupName")?.Remove();
    }

    /// <summary>
    /// Compute TaxonomyGUID directly from a known TagGroupId (from field XML settings).
    /// Used when TagGroupToTaxonomyGuid dict is empty (--tags not run in same process).
    /// </summary>
    private Guid? ResolveFromTagGroupId(int tagGroupId)
    {
        try
        {
            using var ctx = kx13ContextFactory.CreateDbContext();
            var tagGroup = ctx.CmsTagGroups.FirstOrDefault(g => g.TagGroupId == tagGroupId);
            if (tagGroup == null)
            {
                logger.LogWarning("TagGroup ID={Id} not found in K13", tagGroupId);
                return null;
            }
            return GuidV5.NewNameBased(
                new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"),
                tagGroup.TagGroupGuid.ToString()
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve TaxonomyGUID from TagGroupId={Id}", tagGroupId);
            return null;
        }
    }

    /// <summary>
    /// Query K13 directly to find which TagGroup is used most by pages of this class,
    /// then recompute the deterministic TaxonomyGUID (same GuidV5 formula as MigrateTagsCommandHandler).
    /// Used as fallback when --page-types runs in a separate process from --tags.
    /// </summary>
    private Guid? ResolveFromK13(string className)
    {
        try
        {
            using var kx13Context = kx13ContextFactory.CreateDbContext();

            // Find the most-used TagGroupId for documents of this class
            var tagGroupId = (
                from doc in kx13Context.CmsDocuments
                where doc.DocumentTagGroupId.HasValue
                join tree in kx13Context.CmsTrees on doc.DocumentNodeId equals tree.NodeId
                join cls in kx13Context.CmsClasses on tree.NodeClassId equals cls.ClassId
                where cls.ClassName == className
                group doc by doc.DocumentTagGroupId into g
                orderby g.Count() descending
                select g.Key
            ).FirstOrDefault();

            if (tagGroupId == null)
            {
                logger.LogWarning("No DocumentTagGroupId found in K13 for class '{Class}'", className);
                return null;
            }

            var tagGroup = kx13Context.CmsTagGroups.FirstOrDefault(g => g.TagGroupId == tagGroupId.Value);
            if (tagGroup == null)
            {
                logger.LogWarning("TagGroup ID={Id} not found in K13", tagGroupId.Value);
                return null;
            }

            // Same deterministic formula as MigrateTagsCommandHandler.MigrateTaxonomies
            return GuidV5.NewNameBased(
                new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"),
                tagGroup.TagGroupGuid.ToString()
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve TaxonomyGUID from K13 for class '{Class}'", className);
            return null;
        }
    }

    private static Guid? FindTermGuid(string tagName, int? documentTagGroupId)
    {
        // Scoped lookup: ค้นหา tag ใน TagGroup เดียวกับ document ก่อน
        if (documentTagGroupId.HasValue)
        {
            foreach (var (tagId, name) in MigrateTagsCommandHandler.TagIdToName)
            {
                if (name.Equals(tagName, StringComparison.OrdinalIgnoreCase) &&
                    MigrateTagsCommandHandler.TagIdToGroupId.TryGetValue(tagId, out var groupId) &&
                    groupId == documentTagGroupId.Value &&
                    MigrateTagsCommandHandler.TagIdToTermGuid.TryGetValue(tagId, out var guid))
                {
                    return guid;
                }
            }
        }

        // Fallback: ค้นหาทุก group (กรณีไม่มี DocumentTagGroupId หรือหาใน group ไม่เจอ)
        foreach (var (tagId, name) in MigrateTagsCommandHandler.TagIdToName)
        {
            if (name.Equals(tagName, StringComparison.OrdinalIgnoreCase) &&
                MigrateTagsCommandHandler.TagIdToTermGuid.TryGetValue(tagId, out var guid))
            {
                return guid;
            }
        }

        return null;
    }
}
