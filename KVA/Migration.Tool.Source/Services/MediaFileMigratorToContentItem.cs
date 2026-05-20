using System.Collections.Concurrent;
using CMS.ContentEngine.Internal;
using CMS.Core;
using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.UMT.Services;
using Microsoft.Extensions.Logging;
using Migration.Tool.Common;
using Migration.Tool.Common.Abstractions;
using Migration.Tool.Common.Services;
using Migration.Tool.Source.Handlers;
using Migration.Tool.Source.Mappers.ContentItemMapperDirectives;
using Migration.Tool.Source.Model;

namespace Migration.Tool.Source.Services;

public class MediaFileMigratorToContentItem(
    ILogger<MigrateMediaLibrariesCommandHandler> logger,
    ModelFacade modelFacade,
    IAssetFacade assetFacade,
    IImporter importer,
    UserService userService,
    WorkspaceService workspaceService,
    ContentFolderService contentFolderService,
    IEnumerable<ContentItemDirectorBase> directors
    ) : IMediaFileMigrator
{
    public async Task<CommandResult> Handle(MigrateMediaLibrariesCommand request, CancellationToken cancellationToken)
    {
        await MigrateToAssets();
        return new GenericCommandResult();
    }

    private async Task MigrateToAssets()
    {
        var ksMediaFiles = modelFacade.SelectAll<IMediaFile>(" ORDER BY FileLibraryID");
        var ksMediaLibraries = new ConcurrentDictionary<int, IMediaLibrary?>();
        var ksSites = new ConcurrentDictionary<int, ICmsSite?>();
        var contentLanguageRetriever = Service.Resolve<IContentLanguageRetriever>();
        var defaultContentLanguage = await contentLanguageRetriever.GetDefaultContentLanguage();

        var contentLanguageModelRetriever = Service.Resolve<IContentLanguageModelRetriever>();
        var languages = await contentLanguageModelRetriever.Get();

        string[] languageNames = languages
                .Select(x => x.LanguageName)
                .ToArray();

        logger.LogInformation("[Media Libraries] Starting migration. DefaultLanguage={Language}, Languages={Languages}",
            defaultContentLanguage.ContentLanguageName, string.Join(",", languageNames));

        await assetFacade.PreparePrerequisites();

        int totalCount = 0, successCount = 0, errorCount = 0;
        string? currentLibrary = null;
        int librarySuccessCount = 0, libraryErrorCount = 0;

        foreach (var ksMediaFile in ksMediaFiles)
        {
            totalCount++;

            if (ksSites.GetOrAdd(ksMediaFile.FileSiteID, siteId => modelFacade.SelectById<ICmsSite>(siteId)) is not { } ksSite)
            {
                logger.LogError("[Media Libraries] Media file '{FileGuid}' skipped: site ID={SiteId} not found",
                    ksMediaFile.FileGUID, ksMediaFile.FileSiteID);
                errorCount++;
                continue;
            }
            if (ksMediaLibraries.GetOrAdd(ksMediaFile.FileLibraryID, libraryId => modelFacade.SelectById<IMediaLibrary>(libraryId)) is not { } ksMediaLibrary)
            {
                logger.LogError("[Media Libraries] Media file '{FileGuid}' skipped: library ID={LibraryId} not found",
                    ksMediaFile.FileGUID, ksMediaFile.FileLibraryID);
                errorCount++;
                continue;
            }

            // Log library boundary so it's easy to see which library is being processed
            string libraryLabel = $"{ksSite.SiteName}/{ksMediaLibrary.LibraryFolder}";
            if (currentLibrary != libraryLabel)
            {
                if (currentLibrary != null)
                {
                    logger.LogInformation("[Media Libraries] Library '{Library}' done — Success={Success}, Error={Error}",
                        currentLibrary, librarySuccessCount, libraryErrorCount);
                }
                currentLibrary = libraryLabel;
                librarySuccessCount = 0;
                libraryErrorCount = 0;
                logger.LogInformation("[Media Libraries] Processing library '{Library}'", libraryLabel);
            }

            var directive = GetDirective(new(ksSite, ksMediaLibrary, ksMediaFile));

            var workspaceGuid = workspaceService.EnsureWorkspace(directive.WorkspaceOptions);
            var umtContentItem = await assetFacade.FromMediaFile(ksMediaFile, ksMediaLibrary, ksSite, languageNames, workspaceGuid, directive.ContentFolderOptions);

            // Log only essential info (not entire object) to keep log readable
            logger.LogTrace("[Media Libraries] Importing '{FileName}' (Guid={FileGuid}, Library={Library})",
                ksMediaFile.FileName, ksMediaFile.FileGUID, libraryLabel);

            umtContentItem.ContentItemWorkspaceGUID = workspaceGuid;

            if (umtContentItem.LanguageData != null)
            {
                foreach (var item in umtContentItem.LanguageData)
                {
                    item.UserGuid = (item.UserGuid.HasValue && userService.UserExists(item.UserGuid.Value))
                        ? item.UserGuid
                        : userService.DefaultAdminUser?.UserGUID;
                }
            }

            switch (await importer.ImportAsync(umtContentItem))
            {
                case { Success: true }:
                {
                    logger.LogInformation("[Media Libraries] ✅ Imported '{FileName}' (Guid={FileGuid}, Library={Library})",
                        ksMediaFile.FileName, ksMediaFile.FileGUID, libraryLabel);
                    successCount++;
                    librarySuccessCount++;
                    break;
                }
                case { Success: false, Exception: { } exception }:
                {
                    logger.LogError("[Media Libraries] ❌ FAILED '{FileName}' (Guid={FileGuid}, Path={FilePath}, Library={Library}): {Error}",
                        ksMediaFile.FileName, ksMediaFile.FileGUID, ksMediaFile.FilePath, libraryLabel, exception.Message);
                    errorCount++;
                    libraryErrorCount++;
                    break;
                }
                case { Success: false, ModelValidationResults: { } validation }:
                {
                    foreach (var validationResult in validation)
                    {
                        logger.LogError("[Media Libraries] ❌ FAILED '{FileName}' (Guid={FileGuid}, Path={FilePath}, Library={Library}) validation: {Members}: {Error}",
                            ksMediaFile.FileName, ksMediaFile.FileGUID, ksMediaFile.FilePath, libraryLabel,
                            string.Join(",", validationResult.MemberNames), validationResult.ErrorMessage);
                    }
                    errorCount++;
                    libraryErrorCount++;
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        // Final library boundary
        if (currentLibrary != null)
        {
            logger.LogInformation("[Media Libraries] Library '{Library}' done — Success={Success}, Error={Error}",
                currentLibrary, librarySuccessCount, libraryErrorCount);
        }

        // Summary
        if (errorCount > 0)
        {
            logger.LogWarning("[Media Libraries] Migration completed with errors — Total={Total}, Success={Success}, Error={Error}",
                totalCount, successCount, errorCount);
        }
        else
        {
            logger.LogInformation("[Media Libraries] Migration completed successfully — Total={Total}, Success={Success}",
                totalCount, successCount);
        }
    }

    private ContentItemDirectiveBase GetDirective(MediaContentItemSource contentItemSource)
    {
        var directiveFacade = new ContentItemActionProvider();
        foreach (var director in directors)
        {
            director.Direct(contentItemSource, directiveFacade);
            if (directiveFacade.Directive is not null)
            {
                break;
            }
        }
        return directiveFacade.Directive!;
    }
}
