using Kentico.Xperience.UMT.Model;
using Microsoft.Extensions.Logging;
using Migration.Tool.Common.Abstractions;
using Migration.Tool.KX13.Models;

namespace Migration.Tool.Source.Mappers;

/// <summary>
/// Source สำหรับ map K13 CmsTag → XbyK TagModel
/// Dictionary key=TagId, value=TagGuid (ใช้ TagGuid จาก K13 โดยตรง)
/// </summary>
public record CmsTagMapperSource(
    Guid TaxonomyGuid,
    CmsTag CmsTag,
    Dictionary<int, Guid> TagId2Guid
);

/// <summary>
/// Map K13 CmsTag → XbyK TagModel
/// Pattern เดียวกับ TagMapper (Category → Tag) ที่มีอยู่แล้ว
/// </summary>
public class CmsTagMapper(ILogger<CmsTagMapper> logger) : UmtMapperBase<CmsTagMapperSource>
{
    protected override IEnumerable<IUmtModel> MapInternal(CmsTagMapperSource source)
    {
        var (taxonomyGuid, cmsTag, tagId2Guid) = source;

        var tagName = ToCodeName(cmsTag.TagName);

        var tag = new TagModel
        {
            TagGUID = cmsTag.TagGuid,       // ← TagGuid (ไม่ใช่ TagGUID)
            TagName = tagName,
            TagTitle = cmsTag.TagName,
            TagDescription = null,
            TagTaxonomyGUID = taxonomyGuid,
            TagOrder = 0,
            TagParentGUID = null,                  // K13 CmsTag เป็น flat
            TagTranslations = []
        };

        logger.LogTrace(
            "Mapped CmsTag: ID={TagId} '{TagName}' → CodeName='{CodeName}' Taxonomy={TaxGuid}",
            cmsTag.TagId, cmsTag.TagName, tagName, taxonomyGuid
        );

        yield return tag;
    }

    private static string ToCodeName(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return "tag";
        }

        var sb = new System.Text.StringBuilder();
        foreach (var c in tagName.Normalize(System.Text.NormalizationForm.FormD))
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

        // Thai-only name → hex encoding (จำกัด 40 chars)
        if (string.IsNullOrEmpty(result))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(tagName);
            result = Convert.ToHexString(bytes).ToLowerInvariant();
            if (result.Length > 40)
            {
                result = result[..40];
            }
        }

        return string.IsNullOrEmpty(result) ? "tag" : result;
    }
}
