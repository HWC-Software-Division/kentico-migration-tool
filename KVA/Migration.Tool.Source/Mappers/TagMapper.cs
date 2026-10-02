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
        var (taxonomyGuid, cmsCategory, id2Guid) = source;

        // ชื่อที่มีอักขระ non-ASCII (ไทยล้วน/ไทยปนอังกฤษ) ใช้ CategoryGUID จาก K13 เป็น code name
        // แทน hex ที่ถูกตัด 40 ตัว ซึ่งทำให้ category ที่ขึ้นต้นเหมือนกันชนกัน — GUID คงที่ทุกรอบ re-migrate
        string sourceName = cmsCategory.CategoryName ?? cmsCategory.CategoryDisplayName;
        bool hasNonAscii = sourceName.Any(c => c > 127);
        var codeName = hasNonAscii
            ? $"tag_{cmsCategory.CategoryGUID:N}"
            : ToCodeName(sourceName);

        Guid? parentGuid = null;
        if (cmsCategory.CategoryParentID.HasValue &&
            id2Guid.TryGetValue(cmsCategory.CategoryParentID.Value, out var parentGuidValue))
        {
            parentGuid = parentGuidValue;
        }

        var tag = new TagModel
        {
            TagName = cmsCategory.CategoryName,
            TagTitle = cmsCategory.CategoryDisplayName,
            TagDescription = cmsCategory.CategoryDescription,
            TagGUID = cmsCategory.CategoryGUID,
            TagTaxonomyGUID = taxonomyGuid,
            TagOrder = cmsCategory.CategoryOrder ?? 0,
            TagParentGUID = parentGuid,
            TagTranslations = []
        };

        logger.LogTrace(
            "Mapped CmsCategory: ID={CategoryId} '{DisplayName}' → CodeName='{CodeName}' Taxonomy={TaxGuid}",
            cmsCategory.CategoryID, cmsCategory.CategoryDisplayName, codeName, taxonomyGuid
        );


        if (cmsCategory.CategoryParentID is { } categoryParentId)
        {
            if (id2Guid.TryGetValue(categoryParentId, out var categoryGuid))
            {
                tag.TagParentGUID = categoryGuid;
            }
            else
            {
                logger.LogWarning("Missing parent category {CategoryParentID} in source instance", categoryParentId);
            }
        }

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
