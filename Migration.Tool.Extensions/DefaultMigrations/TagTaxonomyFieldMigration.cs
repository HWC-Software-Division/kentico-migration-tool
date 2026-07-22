using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Migration.Tool.Common;
using Migration.Tool.Common.Enumerations;
using Migration.Tool.KXP.Api.Services.CmsClass;

namespace Migration.Tool.Extensions.DefaultMigrations;

/// <summary>
/// IFieldMigration: แปลง K13 DocumentTags (longtext / TagSelector)
///                  → XbyK LongText + Text input field ระหว่าง --page-types
///
/// เก็บ tags เป็น "ข้อความ comma-separated" ตรงๆ เหมือนที่ K13 แสดง
/// (เช่น "เช็กดวง, ดูดวงรายเดือน, สิ่งมงคลประจำวันเกิด") — ไม่ใช้ Taxonomy/Term
///
/// MigrateValue เป็น no-op: DocumentTags เป็น external="true" ใน K13 → sourceValue เป็น null เสมอ
/// ค่าจริงถูกเขียนโดย --tag-values (MigrateTagValuesCommandHandler) จาก CmsDocument.DocumentTags
/// </summary>
public class TagTaxonomyFieldMigration(
    ILogger<TagTaxonomyFieldMigration> logger
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

    public Task<FieldMigrationResult> MigrateValue(
        object? sourceValue,
        FieldMigrationContext context)
    {
        // DocumentTags external="true" ใน K13 → ไม่อยู่ใน coupled data table
        // → MapCoupledDataFieldValues ข้าม → sourceValue เป็น null เสมอ
        // ค่าจริง (comma-separated string) ถูกเขียนทีหลังโดย MigrateTagValuesCommandHandler (--tag-values)
        logger.LogTrace("MigrateValue: DocumentTags skipped (handled by --tag-values)");
        return Task.FromResult(new FieldMigrationResult(true, null));
    }

    public void MigrateFieldDefinition(
        FormDefinitionPatcher formDefinitionPatcher,
        XElement field,
        XAttribute? columnTypeAttr,
        string fieldDescriptor)
    {
        logger.LogInformation("MigrateFieldDefinition (DocumentTags → LongText text): '{Field}'", fieldDescriptor);

        // เก็บ tags เป็นข้อความยาว (nvarchar(max)) — ไม่โดนตัดแม้ tag เยอะ
        columnTypeAttr?.SetValue("longtext");

        // ลบ system="true" เพื่อให้ field แสดงใน Content Types > Fields admin UI
        field.Attribute("system")?.Remove();

        // ลบ external="true" ที่ติดมาจาก K13 (DocumentTags เป็น system field ใน CmsDocument)
        // ใน XbyK: external="true" ทำให้ไม่มี column ในตาราง class-specific → ค่าไม่ถูกบันทึก
        // ต้องลบออกเพื่อให้ XbyK สร้าง DocumentTags column และเก็บข้อความในตารางนั้น
        field.Attribute("external")?.Remove();

        var settings = field.EnsureElement(FormDefinitionPatcher.FieldElemSettings);

        // Text input ธรรมดา (บรรทัดเดียว) ตรงกับ UI ของ K13
        settings.EnsureElement(
            FormDefinitionPatcher.SettingsElemControlname,
            e => e.Value = "Kentico.Administration.TextInput"
        );

        // ลบ settings ของ taxonomy/tag เดิมทั้งหมด (ไม่ใช้ taxonomy แล้ว)
        settings.Element("TaxonomyGroup")?.Remove();
        settings.Element("TaxonomyGUID")?.Remove();
        settings.Element("TagGroupID")?.Remove();
        settings.Element("taggroup")?.Remove();
        settings.Element("TagGroupName")?.Remove();
    }
}
