# สรุปการแก้ไข — branch `20260921_migrateTool` ใน migrate main

ฐาน: `ed84e8cd` (master) → `907377fd` · 6 commits · 41 ไฟล์ · +2,794 / −184 บรรทัด

| Commit | วันที่ | หัวข้อ |
|---|---|---|
| `bc9a09e9` | 2026-09-23 | update setting |
| `657cc8d4` | 2026-09-24 | improved - Media Asset (MediaFile, AssetFacade, MediaFileMigratorToContentItem, MediaLinkService, AssetMigration)
                            improved - allowedContentTypes (AssetMigration - function line 466)
                            improved - Tags (CmsTagMapper, KsCoreDiExtensions, Migration.Tool.Source, TagMapper)
                            added - TagTaxonomyFieldMigration
                            improved - logs (appsettings, Program, MediaFileMigratorToContentItem)
                            improved - dropdown (appsettings, FormDefinitionPatcher, FormFieldMappingModel, FormDefinitionHelper)
                            improved - Visible, VisibilityConditions (FormDefinitionPatcher, FormDefinitionHelper, CmsClassMapper )
                            improved - DependingFields (FormDefinitionPatcher, FormDefinitionHelper)
                            improved - integerToTextConvertedFields (FormDefinitionPatcher, FormFieldMappingModel)
                            improved - chaned dataType GUID to Pages (FormFieldMappingModel, ContentItemMapper)
                            updated - PrimaryKeyLocatorService (line 120)
                            updated - BulkDataCopyService (add function GetSqlTableColumnTypes)
                            updated - Command (Commands, CommandParser , MigrateTagsCommand)
                            improved - FolderDisplayNameToName (ContentFolderService)
                            improved - MigrateContactStatuses, MigrateContactGroups, MigrateContactGroupMembers (MigrateContactManagementCommandHandler)
                            updated - MigrateMembersCommandHandler (line 93-120)
                            updated - MigrateUsersCommandHandler for krungsri (line 29, 83-98 , 111-113)
                            updated - RichText req. of FormFieldMappingModel for krungsri
                            improved - MigrateCustomTablesHandler
                            improved - MigratePagesCommandHandler
                            improved - MigratePageTypesCommandHandler
                            added - MigrateTagsCommandHandler , MigrateTagValuesCommandHandler
                            improved - icon of contentType (CmsClassMapper)
                            improved - ContentItemMapper
                            improved - TagMapper about category
                            fixed bug  - COUNT of NodeGUID (SpoiledGuidContext line 58, ContentItemMapper line 123)
                            improved - Visual Builder widgets mapping of visualBuilderPatcher (ContentItemMapper line 290,675,1556)
                            (commit หลัก) |
| `db60f71e` | 2026-09-25 | (.gitignore / ลบ csproj.user) |
| `0186569e` | 2026-09-25 | fixed bug - case of tableName is null (BulkDataCopyService , MigrateCustomTablesHandler , MigrateCustomModulesCommandHandler , MigrateFormsCommandHandler)
                            improved - visibility condition (FormDefinitionPatcher) |
| `907377fd` | 2026-10-01 | updated – call migrate Tags |
| `4ae5d261` | 2026-10-02 | added - note CHANGES_20260921_migrateTool.md |

---

## 1. `bc9a09e9` — update setting

- อัปเกรด Kentico **31.7.0 → 31.8.2** (`Migration.Tool.KXP.Api`, `Migration.Tool.KXP.Extensions`)
- `appsettings.json` ตั้งค่าเครื่อง local
  - Source: `KrungsriCms2024`, Target: `KrungsriDotcomXbyK_Local`
  - `TargetWorkspaceName` = `"MediaLibrary"`
  - Exclude class `BAY.PageSitemanConfig`
- เพิ่มไฟล์ `appsettings.json.orig` (เศษจาก merge)

## 2. `657cc8d4` — commit หลัก

### Tags / Taxonomy (ใหม่)
- **คำสั่ง `--tags`** (`MigrateTagsCommandHandler`, `MigrateTagsCommand`)
  - `CMS_TagGroup` → Taxonomy, `CMS_Tag` → Tag
  - GUID แบบ deterministic (คงที่ทุกรอบ re-migrate)
  - code name ชน → retry โดยต่อท้าย TagGuid
- **คำสั่ง `--tag-values`** (`MigrateTagValuesCommandHandler`, `MigrateTagValuesCommand`)
  - เขียน K13 `DocumentTags` (comma-separated) ลงคอลัมน์ข้อความ `DocumentTags` ในตารางของ content type โดยตรง — ไม่ใช้ Taxonomy
  - ต้องรันหลัง `--sites --page-types --pages`
- `CmsTagMapper` (ใหม่) — code name = `{group}_{tag}`; ชื่อภาษาไทยใช้ `{group}_{TagGuid}`
- `TagMapper` (Category) — ชื่อ non-ASCII ใช้ `tag_{CategoryGUID}`, เก็บ `TagOrder` และ `TagParentGUID`
- `TagTaxonomyFieldMigration` (ใหม่) — field TagSelector → `longtext` + `Kentico.Administration.TextInput`
- `KsCoreDiExtensions` — register `CmsTagMapper`
- `Migration.Tool.Source.csproj`, `Migration.Tool.Extensions.csproj` — เพิ่ม reference `Migration.Tool.KX13` (ใช้ `KX13Context`)

### Media / Asset
- `MediaFile` — เพิ่มคอลัมน์ `ReleaseDate`
- `AssetFacade` — เพิ่ม field `LegacyMediaFileReleaseDate` (datetime, allow empty) ใน `Legacy.MediaFile`
- `MediaFileMigratorToContentItem` — log ละเอียดขึ้น: แยกตาม library, สรุป Success/Error ต่อ library และรวม
- `MediaLinkService` — กัน index out of range ตอน parse ลิงก์ media
- `AssetMigration`
  - เช็ก `ContentItemExists` ก่อนสร้าง reference (กัน FK violation เมื่อไฟล์ import ไม่สำเร็จ)
  - รองรับ URL `/getmedia/{folder}/{path}` ที่ไม่มี GUID
  - allowed content types ตามชื่อ field:
    - `file` / `upload` / `pdf` → Legacy Attachment
    - `image` / `thumbnail` / `banner` / `teaser` / `logo` → Legacy MediaFile
    - อื่นๆ → MediaFile + MediaLink + Attachment

### Pages / Content Item
- `MigratePagesCommandHandler`
  - node ที่ไม่มี `CMS_Document` → log warning แล้วข้าม (เดิม `Debug.Assert`)
  - ข้าม content item reference ที่เป็น `Guid.Empty` หรือไม่มีอยู่จริง (`CanUseContentItemReference`)
  - เพิ่ม `RepairOrphanedContentItemData` — สร้างแถว placeholder ในตาราง class สำหรับ `ContentItemCommonData` ที่ไม่มีแถวคู่ (แก้ admin เปิดหน้าไม่ได้: `FormDataBinder.Bind` ArgumentNullException)
- `ContentItemMapper`
  - `RenameNodeGuidToWebPageGuid` — แปลง `nodeGuid` → `webPageGuid` ใน JSON ของ Page Builder widgets
  - GUID page selector → WebPages reference; ข้าม GUID ว่าง / ไม่พบใน `CMS_ContentItem`
  - field required ที่ได้ค่า null → ใส่ค่า default (`0` / `false` / `""`)
- `SpoiledGuidContext` — แก้ bug นับ NodeGUID ซ้ำ: `COUNT(NodeGUID)` → `COUNT(DISTINCT NodeID)`
- `ContentFolderService.StandardFolderTemplate` — code name ต่อท้าย 8 ตัวแรกของ folder GUID, ชื่อไทยใช้ `folder_{guid}`, จำกัดความยาว ≤ 50 (แก้ "Unable to obtain unique name")
- `MigratePageTypesCommandHandler` — ล้าง `ClassInheritsFromClassID` / `ClassInheritsFromClass`

### Form definition / Visibility condition
- `FormDefinitionPatcher`
  - แปลง K13 visible macro → XbyK `VisibilityCondition` (`==`, null/empty, bool, integer, decimal, string, `Contains`/`StartsWith`/`EndsWith`)
  - field integer ที่ถูกแปลงเป็น text → condition ใช้การเทียบแบบ string
  - Custom table: เก็บ `<settings>`, `hasdependingfields` / `dependsonanotherfield` และ `visible` เดิมไว้
  - field ที่มี default value → ไม่บังคับ required
- `FormDefinitionHelper`
  - `ApplyVisibilityConditions`, `ReinjectDependencyAttributes`
  - `EnsureVisibilityConditionOrdering` — ย้าย field ที่ถูกอ้างอิงใน condition ให้อยู่ก่อน field ที่ใช้ condition (XbyK บังคับ)
- `FormFieldMappingModel` + `appsettings.json` (`CustomMigration.FieldMigrations`)
  - Integer + DropDown/RadioButtons → Text + DropDown/RadioGroup
  - **RichText / HtmlArea → LongText + TextArea** (สำหรับ krungsri)
  - Guid → WebPages selector (`ConvertToPages`)
- `CmsClassMapper` — field ที่มี visibility condition → `AllowEmpty = true`

### Contact Management / Users / Members
- `MigrateContactManagementCommandHandler`
  - เพิ่ม migrate `OM_ContactStatus` (ก่อน contacts), `OM_ContactGroup`, `OM_ContactGroupMember`
  - `ContactStatusID = 0` → `NULL` (กัน FK)
- `MigrateUsersCommandHandler` — user ไม่มี email → สร้าง `{username}@krungsri.com` (log warning แทน error)
- `MigrateMembersCommandHandler` — email ซ้ำ → ล้าง email แล้ว save ใหม่

### อื่นๆ
- `MigrateCustomTablesHandler` — ทนต่อ XML schema เสีย / auto-increment column ≠ 1; แปลง Guid → string เมื่อคอลัมน์ปลายทางเป็น nvarchar/varchar
- `BulkDataCopyService` — เพิ่ม `GetSqlTableColumnTypes`
- `PrimaryKeyLocatorService` (บรรทัด ~120) — `ICmsResource` หา `ClassID` จาก `CMS_Class` แทน `ResourceInfo`
- `Commands` / `CommandParser` — เพิ่ม `--tags`, `--tag-values`, `--customers`, `--orders`
- `Program.cs` + `appsettings.json` — log แยก 3 ไฟล์
  - `logs/migration.tool.log` (full)
  - `logs/migration.tool-warning.log` (Warning + Error)
  - `logs/migration.tool-error.log` (Error)

## 3. `db60f71e`
- `.gitignore` เพิ่ม `/Migration.Tool.CLI/Properties/PublishProfiles` และ `/Migration.Tool.CLI/Migration.Tool.CLI.csproj.user`
- ลบ `Migration.Tool.CLI.csproj.user` ออกจาก repo
- *หมายเหตุ: ข้อความ commit ซ้ำกับ `0186569e` แต่เนื้อหาจริงมีแค่ 2 รายการข้างต้น*

## 4. `0186569e` — fixed bug: tableName is null
- `MigrateCustomModulesCommandHandler`, `MigrateCustomTablesHandler`, `MigrateFormsCommandHandler` — `Debug.Assert(ClassTableName != null)` → log error แล้วข้าม class
- `FormDefinitionPatcher` — `IndexOfMacroSecuritySignature` ตัด `|(identity)…`, `|(user)…`, `|(hash)…` ออกจาก macro ทั้งแบบมีและไม่มี `{% %}`
- `appsettings.json` — target DB → `KrungsriDotcomXbyK_Local5`

## 5. `907377fd` — call migrate Tags
- `ServiceCollectionExtensions` — register `TagTaxonomyFieldMigration` เป็น `IFieldMigration`

---

## ลำดับการรันที่แนะนำ (ส่วนที่เกี่ยวกับ Tags)

```
migrate --sites --users --tags --page-types --pages --tag-values
```

---

## ข้อสังเกต / สิ่งที่ควรตรวจก่อน push

1. **`appsettings.json` มี connection string + `sa/1234`** ถูก commit และมี `appsettings.json.orig` ติดมา — ควรย้ายไป `appsettings.local.json` (ignored) หรือเอาออก
2. `CommandParser.PrintCommandDescriptions` — บรรทัด Customers/Orders ซ้ำ (help แสดง 2 ครั้ง)
3. ข้อความ commit `db60f71e` ไม่ตรงกับเนื้อหา; ใน `0186569e` อ้าง `BulkDataCopyService` แต่ไม่ได้แก้ไฟล์นั้น
4. comment ใน `ContentItemMapper` (~บรรทัด 352) บอกว่า `--tag-values` insert ลง `CMS_ContentItemTag` — ไม่ตรงกับโค้ดปัจจุบัน (เขียนเป็นข้อความ)
5. `MediaLinkService` ยังมี `Console.WriteLine` debug และข้อความ "parse skipped" ปรากฏใน path ที่ parse สำเร็จ
6. `PrimaryKeyLocatorService` ใช้ ResourceGUID ไปหาใน `CMS_Class.ClassGUID` — ถ้า GUID ไม่ตรง `.Single()` จะ throw ควรยืนยันว่าตั้งใจ
7. `AssetMigration` เลิกตั้ง `AllowEmpty=true` + min/max items = 1 ให้ field asset — พฤติกรรมเปลี่ยนจากเดิม
