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
            target.ClassFormDefinition = ReinjectDependencyAttributes(formInfo.GetXmlDefinition(), patcher.GetPendingDependencyAttributes());
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
            target.ClassFormDefinition = formInfo.GetXmlDefinition();
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
}
