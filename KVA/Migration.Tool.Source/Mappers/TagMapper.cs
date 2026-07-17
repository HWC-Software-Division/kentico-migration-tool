using Kentico.Xperience.UMT.Model;
using Microsoft.Extensions.Logging;
using Migration.Tool.Common.Abstractions;
using Migration.Tool.Source.Model;

namespace Migration.Tool.Source.Mappers;

public record TagModelSource(Guid TaxonomyGuid, ICmsCategory CmsCategory, Dictionary<int, Guid> CategoryId2Guid);

public class TagMapper(ILogger<TagMapper> logger) : UmtMapperBase<TagModelSource>
{
    protected override IEnumerable<IUmtModel> MapInternal(TagModelSource source)
    {
        var (taxonomyGuid, category, categoryId2Guid) = source;

        // ชื่อที่มีอักขระ non-ASCII (ไทยล้วน/ไทยปนอังกฤษ) ใช้ CategoryGUID จาก K13 เป็น code name
        // แทน hex ที่ถูกตัด 40 ตัว ซึ่งทำให้ category ที่ขึ้นต้นเหมือนกันชนกัน — GUID คงที่ทุกรอบ re-migrate
        string sourceName = category.CategoryName ?? category.CategoryDisplayName;
        bool hasNonAscii = sourceName.Any(c => c > 127);
        var codeName = hasNonAscii
            ? $"tag_{category.CategoryGUID:N}"
            : ToCodeName(sourceName);

        Guid? parentGuid = null;
        if (category.CategoryParentID.HasValue &&
            categoryId2Guid.TryGetValue(category.CategoryParentID.Value, out var parentGuidValue))
        {
            parentGuid = parentGuidValue;
        }

        var tag = new TagModel
        {
            TagGUID = category.CategoryGUID,
            TagName = codeName,
            TagTitle = category.CategoryDisplayName,
            TagDescription = category.CategoryDescription,
            TagTaxonomyGUID = taxonomyGuid,
            TagOrder = category.CategoryOrder ?? 0,
            TagParentGUID = parentGuid,
            TagTranslations = []
        };

        logger.LogTrace(
            "Mapped CmsCategory: ID={CategoryId} '{DisplayName}' → CodeName='{CodeName}' Taxonomy={TaxGuid}",
            category.CategoryID, category.CategoryDisplayName, codeName, taxonomyGuid
        );

        yield return tag;
    }

    private static string ToCodeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "tag";
        }

        var sb = new System.Text.StringBuilder();
        foreach (var c in name.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (c < 128)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
                else if (char.IsWhiteSpace(c) || c == '-' || c == '_')
                {
                    sb.Append('-');
                }
            }
        }

        var result = sb.ToString().Trim('-');

        if (string.IsNullOrEmpty(result))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(name);
            result = Convert.ToHexString(bytes).ToLowerInvariant();
            if (result.Length > 40)
            {
                result = result[..40];
            }
        }

        return string.IsNullOrEmpty(result) ? "tag" : result;
    }
}
