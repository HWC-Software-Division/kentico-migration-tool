This repository is clone from the [Xperience by Kentico Migration Toolkit](https://github.com/Kentico/xperience-by-kentico-migration-toolkit).

We will use to custom the migration-tool for migrate Kentico 13 to Xperience by Kentico (XbyK)

**List of improvements:**
1. Improved Kylie migration conditions : Media file and MigratePages
  + KVAMigration.Tool.Source\Handlers
    - MigratePagesCommandHandler.cs
  + Migration.Tool.Common\Helpers
    - MediaLinkService.cs

2. Improved Firn migration conditions : support multiple languages media file (contnet hub)
   + KVAMigration.Tool.Source\Services
     - MediaFileMigratorToContentitem.cs
       
3. Improved Firn migration conditions : support custom field Media file (LegacyMediaFileReleaseDateField)
   + KVAMigration.Tool.Source\Model
     - MediaFile.cs
   + KVAMigration.Tool.Source\Services
     - AssetFacade.cs

4. Improved Firn migration conditions : support re-migrate case of multi workspace
   + Migration.Tool.CLI\appsettings.json
    "TargetWorkspaceName": "KenticoDefault" //Your Default channel name

5. Improved Firn migration conditions : support Media file about AllowedContentTypes
   + Migration.Tool.Extensions\DefaultMigrations\AssetMigration.cs  
