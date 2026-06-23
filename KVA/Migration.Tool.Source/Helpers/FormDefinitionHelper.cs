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
                classIsCustom,
                // Custom tables keep their original K13 field definition (settings + dropdown
                // dependency attributes) verbatim for reference.
                preserveSourceFieldDefinition: source.ClassIsCustomTable
            )
            {
                CurrentClassName = source.ClassName
            };

            patcher.PatchFields();
            patcher.RemoveCategories(); // TODO tk: 2022-10-11 remove when supported

            string? result = patcher.GetPatched();
            if (isCustomizableSystemClass)
            {
                result = FormHelper.MergeFormDefinitions(target.ClassFormDefinition, result);
            }

            var formInfo = new FormInfo(result);
            ApplyVisibilityConditions(formInfo, patcher.GetPendingVisibilityConditions());
            target.ClassFormDefinition = EnsureVisibilityConditionOrdering(
                ReinjectDependencyAttributes(formInfo.GetXmlDefinition(), patcher.GetPendingDependencyAttributes()),
                logger, source.ClassName);
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

    /// <summary>
    /// Re-applies K13 dropdown dependency attributes (hasdependingfields / dependsonanotherfield) to the
    /// final form definition. The FormInfo round-trip strips field attributes it does not recognise, so for
    /// custom tables these are restored here, matched by field GUID.
    /// </summary>
    private static string ReinjectDependencyAttributes(string formDefinitionXml,
        IReadOnlyDictionary<string, (string? HasDependingFields, string? DependsOnAnotherField)> dependencyAttributes)
    {
        if (dependencyAttributes.Count == 0)
        {
            return formDefinitionXml;
        }

        var doc = XDocument.Parse(formDefinitionXml);
        foreach (var field in doc.Descendants("field"))
        {
            if (field.Attribute("guid")?.Value is { } guid && dependencyAttributes.TryGetValue(guid, out var attrs))
            {
                if (attrs.HasDependingFields != null)
                {
                    field.SetAttributeValue(FormDefinitionPatcher.FieldAttrHasDependingFields, attrs.HasDependingFields);
                }
                if (attrs.DependsOnAnotherField != null)
                {
                    field.SetAttributeValue(FormDefinitionPatcher.FieldAttrDependsOnAnotherField, attrs.DependsOnAnotherField);
                }
            }
        }

        return doc.ToString(SaveOptions.DisableFormatting);
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
    /// Called AFTER ApplyVisibilityConditions() so that visibilityconditiondata elements
    /// are already present in the XML. Iterates until ordering is stable.
    /// </summary>
    private static string EnsureVisibilityConditionOrdering(string formDefinitionXml, ILogger logger, string? className)
    {
        if (string.IsNullOrWhiteSpace(formDefinitionXml))
        {
            return formDefinitionXml;
        }

        XDocument xDoc;
        try
        { xDoc = XDocument.Parse(formDefinitionXml); }
        catch { return formDefinitionXml; }

        if (xDoc.Root is null)
        {
            return formDefinitionXml;
        }

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
                if (col != null)
                {
                    colToIndex[col] = i;
                }
            }

            for (int i = 0; i < fields.Count; i++)
            {
                var vcData = fields[i].Element("visibilityconditiondata");
                if (vcData is null)
                {
                    continue;
                }

                var refFieldName = vcData.Descendants("PropertyName").FirstOrDefault()?.Value;
                if (string.IsNullOrEmpty(refFieldName))
                {
                    continue;
                }

                if (!colToIndex.TryGetValue(refFieldName, out var refIdx))
                {
                    continue;
                }

                if (refIdx < i)
                {
                    continue;
                }

                var refElement = fields[refIdx];
                refElement.Remove();
                fields[i].AddBeforeSelf(refElement);

                logger.LogWarning(
                    "Reordered field '{RefField}' to precede '{Field}' (class '{Class}') — " +
                    "XbyK requires visibility-condition target fields to precede the field that depends on them.",
                    refFieldName, fields[i].Attribute(fieldAttrColumn)?.Value, className ?? "<unknown>");

                changed = true;
                break;
            }
        }

        return xDoc.Root.ToString();
    }
}
