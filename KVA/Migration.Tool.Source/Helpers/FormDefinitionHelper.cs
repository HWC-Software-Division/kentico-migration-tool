using System.Text.RegularExpressions;
using System.Xml.Linq;
using CMS.DataEngine;
using CMS.FormEngine;
using Microsoft.Extensions.Logging;
using Migration.Tool.KXP.Api.Services.CmsClass;
using Migration.Tool.Source.Model;

namespace Migration.Tool.Source.Helpers;

public static class FormDefinitionHelper
{
    public static void MapFormDefinitionFields(ILogger logger, IFieldMigrationService fieldMigrationService, ICmsClass source, DataClassInfo target, bool isCustomizableSystemClass, bool classIsCustom)
    {
        if (!string.IsNullOrWhiteSpace(source.ClassFormDefinition))
        {
            var patcher = new FormDefinitionPatcher(
                logger,
                source.ClassFormDefinition,
                fieldMigrationService,
                source.ClassIsForm.GetValueOrDefault(false),
                source.ClassIsDocumentType,
                isCustomizableSystemClass,
                classIsCustom
            );
            patcher.CurrentClassName = source.ClassName;

            patcher.PatchFields();
            patcher.RemoveCategories(); // TODO tk: 2022-10-11 remove when supported

            string? result = patcher.GetPatched();
            if (isCustomizableSystemClass)
            {
                result = FormHelper.MergeFormDefinitions(target.ClassFormDefinition, result);
            }

            var formInfo = new FormInfo(result);
            ApplyVisibilityConditions(formInfo, patcher.GetPendingVisibilityConditions());
            target.ClassFormDefinition = EnsureVisibilityConditionOrdering(formInfo.GetXmlDefinition(), logger, source.ClassName);
        }
        else
        {
            target.ClassFormDefinition = new FormInfo().GetXmlDefinition();
        }
    }

    public static void MapFormDefinitionFields(ILogger logger, IFieldMigrationService fieldMigrationService,
        string sourceClassDefinition, bool? classIsForm, bool classIsDocumentType,
        DataClassInfo target, bool isCustomizableSystemClass, bool classIsCustom, IEnumerable<string> excludedFields)
    {
        if (!string.IsNullOrWhiteSpace(sourceClassDefinition))
        {
            var patcher = new FormDefinitionPatcher(
                logger,
                sourceClassDefinition,
                fieldMigrationService,
                classIsForm.GetValueOrDefault(false),
                classIsDocumentType,
                isCustomizableSystemClass,
                classIsCustom
            );

            patcher.PatchFields(excludedFields);
            patcher.RemoveCategories(); // TODO tk: 2022-10-11 remove when supported

            string? result = patcher.GetPatched();
            if (isCustomizableSystemClass)
            {
                result = FormHelper.MergeFormDefinitions(target.ClassFormDefinition, result);
            }

            var formInfo = new FormInfo(result);
            ApplyVisibilityConditions(formInfo, patcher.GetPendingVisibilityConditions());
            target.ClassFormDefinition = EnsureVisibilityConditionOrdering(formInfo.GetXmlDefinition(), logger, null);
        }
        else
        {
            target.ClassFormDefinition = new FormInfo().GetXmlDefinition();
        }
    }

    private static void ApplyVisibilityConditions(FormInfo formInfo, IReadOnlyDictionary<string, string> conditions)
    {
        foreach (var (fieldName, conditionXml) in conditions)
        {
            if (formInfo.GetFormField(fieldName) is { } ffi)
            {
                ffi.VisibilityConditionConfigurationXmlData = conditionXml;
                ffi.AllowEmpty = true; // field with a visibility condition must not be required when hidden
                formInfo.UpdateFormField(fieldName, ffi);
            }
        }
    }

    /// <summary>
    /// XbyK requires that any field referenced by a visibility condition must appear
    /// logically before the field that owns the condition.  K13 ClassFormDefinition XML
    /// sometimes places the referenced field AFTER the dependent field.
    ///
    /// This method is called AFTER ApplyVisibilityConditions() so that
    /// visibilityconditiondata elements are already present in the XML.
    /// It iterates until ordering is stable, moving each referenced field
    /// to just before the first field that depends on it.
    /// </summary>
    private static string EnsureVisibilityConditionOrdering(string formDefinitionXml, ILogger logger, string? className)
    {
        if (string.IsNullOrWhiteSpace(formDefinitionXml)) return formDefinitionXml;

        XDocument xDoc;
        try { xDoc = XDocument.Parse(formDefinitionXml); }
        catch { return formDefinitionXml; }

        if (xDoc.Root is null) return formDefinitionXml;

        const string fieldElem = "field";
        const string fieldAttrColumn = "column";

        bool changed = true;
        int maxPasses = xDoc.Root.Elements(fieldElem).Count() + 1;

        for (int pass = 0; pass < maxPasses && changed; pass++)
        {
            changed = false;
            var fields = xDoc.Root.Elements(fieldElem).ToList();

            var colToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < fields.Count; i++)
            {
                var col = fields[i].Attribute(fieldAttrColumn)?.Value;
                if (col != null) colToIndex[col] = i;
            }

            for (int i = 0; i < fields.Count; i++)
            {
                var vcData = fields[i].Element("visibilityconditiondata");
                if (vcData is null) continue;

                // <PropertyName>FieldName</PropertyName> — the field this condition depends on
                var refFieldName = vcData.Descendants("PropertyName").FirstOrDefault()?.Value;
                if (string.IsNullOrEmpty(refFieldName)) continue;

                if (!colToIndex.TryGetValue(refFieldName, out var refIdx)) continue;
                if (refIdx < i) continue; // already in correct order

                // Move the referenced field to just before the field that depends on it
                var refElement = fields[refIdx];
                refElement.Remove();
                fields[i].AddBeforeSelf(refElement);

                logger.LogWarning(
                    "Reordered field '{RefField}' to precede '{Field}' (class '{Class}') — " +
                    "XbyK requires visibility-condition target fields to precede the field that depends on them.",
                    refFieldName, fields[i].Attribute(fieldAttrColumn)?.Value, className ?? "<unknown>");

                changed = true;
                break; // restart scan with updated field list
            }
        }

        return xDoc.Root.ToString();
    }
}
