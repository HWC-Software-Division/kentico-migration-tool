//using CMS.ContentEngine;
//using Kentico.Xperience.UMT.Model;
//using Kentico.Xperience.UMT.Services;
//using MediatR;
//using Microsoft.Extensions.Logging;
//using Migration.Tool.Common;
//using Migration.Tool.Common.Abstractions;
//using Migration.Tool.Common.MigrationProtocol;
//using Migration.Tool.Source.Contexts;
//using Migration.Tool.Source.Mappers;

//namespace Migration.Tool.Source.Handlers;

//public class MigrateTagsCommandHandler(
//    ILogger<MigrateTagsCommandHandler> logger,
//    ModelFacade modelFacade,
//    IImporter importer,
//    IProtocol protocol,
//    PrimaryKeyMappingContext primaryKeyMappingContext,
//    TagMapper tagMapper
//    )
//    : IRequestHandler<MigratePageTypesCommand, CommandResult>
//{
//    public async Task<CommandResult> Handle(MigrateTagsCommand request, CancellationToken cancellationToken)
//    {
//        logger.LogInformation("==== START MIGRATE TAGS ====");

//        // =========================
//        // 1. LOAD TAG GROUP
//        // =========================
//        var tagGroups = modelFacade.SelectAll<ICmsTagGroup>().ToList();

//        var taxonomyMap = new Dictionary<int, Guid>(); // TagGroupID → TaxonomyGUID

//        foreach (var group in tagGroups)
//        {
//            protocol.FetchedSource(group);

//            var taxonomyModel = new TaxonomyModel
//            {
//                TaxonomyName = group.TagGroupDisplayName ?? group.TagGroupName,
//                TaxonomyCodeName = group.TagGroupName
//            };

//            var result = await importer.ImportAsync(taxonomyModel);

//            if (!result.Success)
//            {
//                logger.LogError("Failed to create taxonomy {Name}", group.TagGroupName);
//                continue;
//            }

//            logger.LogInformation("Created taxonomy: {Name}", group.TagGroupName);

//            taxonomyMap[group.TagGroupID] = taxonomyModel.TaxonomyGUID;

//            primaryKeyMappingContext.SetMapping<TaxonomyInfo>(
//                t => t.TaxonomyID,
//                group.TagGroupID,
//                taxonomyModel.TaxonomyID
//            );
//        }

//        // =========================
//        // 2. LOAD TAG
//        // =========================
//        var tags = modelFacade.SelectAll<ICmsTag>().ToList();

//        foreach (var tag in tags)
//        {
//            protocol.FetchedSource(tag);

//            if (!taxonomyMap.TryGetValue(tag.TagGroupID, out var taxonomyGuid))
//            {
//                logger.LogWarning("Skip tag {Tag}, taxonomy not found", tag.TagName);
//                continue;
//            }

//            var termModel = new TaxonomyTermModel
//            {
//                TaxonomyTermName = tag.TagName.Trim(),
//                TaxonomyTermTaxonomyGUID = taxonomyGuid
//            };

//            var result = await importer.ImportAsync(termModel);

//            if (!result.Success)
//            {
//                logger.LogError("Failed to create term {Tag}", tag.TagName);
//                continue;
//            }

//            logger.LogInformation(
//                "Created term: {Tag} (TaxonomyID: {Taxonomy})",
//                tag.TagName,
//                taxonomyGuid
//            );

//            primaryKeyMappingContext.SetMapping<TaxonomyTermInfo>(
//                t => t.TaxonomyTermID,
//                tag.TagID,
//                termModel.TaxonomyTermID
//            );
//        }

//        logger.LogInformation("==== END MIGRATE TAGS ====");

//        return new GenericCommandResult();
//    }
//}
