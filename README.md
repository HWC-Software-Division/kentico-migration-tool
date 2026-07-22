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

---

## Change log (May – Jul 2026)

รายการปรับแก้เพิ่มเติม เรียงตามหัวข้อ (อ้างอิงจาก git history)

### 6. Migrate Tags : K13 `CMS_TagGroup` / `CMS_Tag` → XbyK Taxonomy / Terms
Migrate กลุ่มแท็กและแท็กจาก K13 มาเป็น Taxonomy และ Taxonomy Terms ใน XbyK พร้อม migrate ค่าแท็กของแต่ละหน้า
   + KVA\Migration.Tool.Source\Handlers
     - MigrateTagsCommandHandler.cs
   + KVA\Migration.Tool.Source\Mappers
     - CmsTagMapper.cs  (CMS_Tag → Term)
     - TagMapper.cs     (CMS_Category → Tag)
   + KVA\Migration.Tool.Source\Handlers
     - MigrateTagValuesCommandHandler.cs
   + Migration.Tool.Extensions\DefaultMigrations
     - TagTaxonomyFieldMigration.cs

### 7. Fixed Tags codeName (duplicate / ภาษาไทย / ชื่อพ้อง)
แก้ปัญหา `CodeNameNotUniqueException: tag code name already exists` (เดิม fail 1,564 → 0)
   + KVA\Migration.Tool.Source\Mappers\CmsTagMapper.cs , TagMapper.cs
     - เติม code name ของ TagGroup เป็น prefix กันชนข้ามกลุ่ม : `{group}_{tag}`
     - ชื่อที่มีอักขระ non-ASCII (ไทย/ไทยปนอังกฤษ) ใช้ `TagGuid`/`CategoryGUID` เดิมจาก K13 เป็น code name แทน hex ที่ถูกตัด 40 ตัว (คงที่ทุกรอบ re-migrate) — `TagTitle` ยังเก็บชื่อไทยไว้แสดงผล
   + KVA\Migration.Tool.Source\Handlers\MigrateTagsCommandHandler.cs
     - เมื่อ code name ชน (เช่น "LGBTQ+" กับ "LGBTQ") retry ด้วย `_{TagGuid}` ต่อท้าย ให้เข้าครบ
     - ข้าม tag ที่ไม่มีชื่อ (ข้อมูลขยะจาก K13) เป็น warning แทน error

### 8. Fixed Content Folder (media) name — โฟลเดอร์ไทย / ชื่อซ้ำ / ยาวเกิน
แก้ media migration crash `Unable to obtain unique name` และ `CodeNameNotValidException` ของ media library ที่มีโฟลเดอร์ภาษาไทยหรือชื่อซ้ำ
   + Migration.Tool.Common\Services\ContentFolderService.cs
     - ชื่อโฟลเดอร์ที่มี non-ASCII (ไทย) → ใช้ folder GUID เป็น code name (`ContentFolderDisplayName` ยังเป็นไทยให้ผู้ใช้เห็น)
     - ชื่อโฟลเดอร์ซ้ำทั่ว tree (เช่น `pdf`/`en`/`th` ใต้ทุก bulletin) → ต่อท้าย code name ด้วย 8 ตัวแรกของ GUID (deterministic ต่อ path) กันหมด suffix pool ของ `UniqueNameHelper` (460 แบบ)
     - จำกัด `ContentFolderName` ไม่เกิน 50 ตัวอักษร (ตัดชื่อฐานก่อนต่อ suffix)

### 9. Fixed RichText / LongText field → nvarchar(max) (กัน content ยาวโดนตัด)
แก้ `String or binary data would be truncated` ของ field เนื้อหา HTML ยาว (ArticleBody, Detail, SummaryText, ContentDetail ฯลฯ)
   + Migration.Tool.KXP.Api\Services\CmsClass\FormFieldMappingModel.cs
     - `RichTextHTML` และ `LongText (HtmlAreaControl)` map เป็น `FieldDataType.LongText` (nvarchar(max)) แทน `Text` (มีเพดาน) — ยังใช้ Form component เป็น Text area เหมือนเดิม

### 10. Visibility conditions & field type (K13 → XbyK)
   + migrate K13 field visibility macros → XbyK XML ; แก้ลำดับ field ของ visibility condition (target field ต้องมาก่อน) ; แปลง field int → text (Dropdown/Radio)

### 11. Custom Tables (ปรับปรุงหลายรอบ)
   + KVA\Migration.Tool.Source\Handlers\MigrateCustomTablesHandler.cs
     - เก็บ controlname เดิมของ K13 ; เก็บ FormInfo settings ครบ (Autoresize, MediaDialogConfiguration, min/max/step ฯลฯ) ; รองรับ `<visible>` attribute ; แก้ error ตอน re-migrate ; แก้ ItemGUID type mismatch

### 12. Robustness / logging
   + แยก log ตาม type (full / warning / error) ; กัน page import crash จาก media file content item ที่หาย ; รองรับ fileUpload ค่า null ; contact & activity migration (contact management)
