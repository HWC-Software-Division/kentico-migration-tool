using System.Xml.Linq;
using System.Xml.XPath;
using CMS.Core;
using CMS.EventLog;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

using Migration.Tool.Common;
using Migration.Tool.Common.Enumerations;

namespace Migration.Tool.KXP.Api.Services.CmsClass;

public class FormDefinitionPatcher
{
    public const string CategoryElem = "category";
    public const string CategoryAttrName = FieldAttrName;
    public const string FieldAttrColumn = "column";
    public const string FieldAttrColumntype = "columntype";
    public const string FieldAttrEnabled = "enabled";
    public const string FieldAttrGuid = "guid";
    public const string FieldAttrIspk = "isPK";
    public const string FieldAttrName = "name";
    public const string FieldAttrSize = "size";
    public const int FieldAttrSizeZero = 0;
    public const string FieldAttrSystem = "system";
    public const string FieldAttrVisible = "visible";
    public const string FieldElem = "field";
    public const string FieldElemProperties = "properties";
    public const string FieldElemSettings = "settings";
    public const string AllowedContentItemTypeIdentifiers = "AllowedContentItemTypeIdentifiers";
    public const string PropertiesElemDefaultvalue = "defaultvalue";
    public const string PropertiesElemFieldcaption = "fieldcaption";
    public const string SettingsElemControlname = "controlname";
    public const string SettingsMaximumassets = "MaximumAssets";
    public const string SettingsMaximumassetsFallback = "99";
    public const string SettingsMaximumpages = "MaximumPages";
    public const string SettingsMaximumpagesFallback = "99";
    public const string SettingsRootpath = "RootPath";
    public const string SettingsRootpathFallback = "/";
    public const string FieldAttrAllowEmpty = "allowempty";
    public const string FieldAttrColumnsize = "columnsize";
    public const string PropertiesElemVisiblemacro = "visiblemacro";
    public const string FieldElemVisibilityConditionData = "visibilityconditiondata";

    private readonly IReadOnlySet<string> allowedFieldAttributes = new HashSet<string>([
        // taken from FormFieldInfo.GetAttributes() method
        "column",
        "visible",
        "enabled",
        "columntype",
        "allowempty",
        "isPK",
        "system",
        "columnsize",
        "columnprecision",
        "guid",
        "external",
        "isinherited",
        "mappedtofield",
        "dummy",
        "isunique",
        "refobjtype",
        "reftype",
        "resolvedefaultvalue"
    ], StringComparer.InvariantCultureIgnoreCase);

    private readonly bool altForm;
    private readonly bool allowNullSourceFormControl;
    private readonly bool classIsCustom;
    private readonly bool classIsDocumentType;
    private readonly bool classIsForm;
    private readonly bool discardSysFields;
    private readonly IFieldMigrationService fieldMigrationService;
    private readonly string formDefinitionXml;

    private readonly ILogger logger;
    private readonly XDocument xDoc;
    private readonly Dictionary<string, string> pendingVisibilityConditions = new();
    // Integer fields converted to text (due to dropdown/radio control) — visibility conditions
    // that reference these fields must use string comparison instead of integer comparison.
    private readonly HashSet<string> integerToTextConvertedFields = new(StringComparer.OrdinalIgnoreCase);

    public FormDefinitionPatcher(
        ILogger logger,
        string formDefinitionXml,
        IFieldMigrationService fieldMigrationService,
        bool classIsForm,
        bool classIsDocumentType,
        bool discardSysFields,
        bool classIsCustom,
        bool altForm = false,
        bool allowNullSourceFormControl = false)
    {
        this.logger = logger;
        this.formDefinitionXml = formDefinitionXml;
        this.fieldMigrationService = fieldMigrationService;
        this.classIsForm = classIsForm;
        this.classIsDocumentType = classIsDocumentType;
        this.discardSysFields = discardSysFields;
        this.classIsCustom = classIsCustom;
        this.altForm = altForm;
        this.allowNullSourceFormControl = allowNullSourceFormControl;
        xDoc = XDocument.Parse(this.formDefinitionXml);
    }

    public IEnumerable<string?> GetFieldNames() =>
        xDoc.XPathSelectElements($"//{FieldElem}")
            .Select(x => x.Attribute(FieldAttrColumn)?.Value);

    public void RemoveCategories()
    {
        var categories = (xDoc.Root?.XPathSelectElements($"//{CategoryElem}") ?? Enumerable.Empty<XElement>()).ToList();

        foreach (var xElement in categories)
        {
            string elementDescriptor = xElement.ToString();

            if (xElement.Attribute(FieldAttrName)?.Value is { } name)
            {
                elementDescriptor = name;
            }

            logger.LogDebug("Removing category '{CategoryDescriptor}'", elementDescriptor);
            xElement.Remove();
        }
    }

    public void RemoveFields(string diffAgainstDefinition)
    {
        var otherDoc = XDocument.Parse(diffAgainstDefinition);

        if (otherDoc.Root?.Elements() is { } elements)
        {
            var elementList = elements.ToList();

            foreach (var field in elementList)
            {
                if (field.Attribute(FieldAttrColumn)?.Value is { } fieldToRemoveName)
                {
                    var fieldsToRemove = xDoc
                        .XPathSelectElements($"//{FieldElem}[@column='{fieldToRemoveName}']")
                        .ToList();

                    if (fieldsToRemove.Count > 0)
                    {
                        logger.LogDebug("Field {FieldName} removed from definition", fieldToRemoveName);

                        foreach (var fieldToRemove in fieldsToRemove)
                        {
                            fieldToRemove.Remove();
                        }
                    }
                    else
                    {
                        logger.LogDebug("Field {FieldName} not found, cannot remove from definition", fieldToRemoveName);
                    }
                }
            }
        }
        else
        {
            logger.LogError("Unable to parse form definition: {FormDefinition}", diffAgainstDefinition);
        }
    }

    public void PatchFields(IEnumerable<string>? excludedFields = null)
    {
        if (xDoc.Root?.Elements() is { } elements)
        {
            var elementList = elements.ToList();

            foreach (var fieldOrCategory in elementList)
            {
                if (fieldOrCategory.Name == FieldElem)
                {
                    if (!(excludedFields ?? Array.Empty<string>()).Any(ef =>
                            ef.Equals(fieldOrCategory.Attribute(FieldAttrColumn)?.Value, StringComparison.InvariantCultureIgnoreCase)))
                    {
                        PatchField(fieldOrCategory);
                    }
                }
                else if (fieldOrCategory.Name == CategoryElem)
                {
                    logger.LogDebug(
                        "Category '{Category}' skipped",
                        fieldOrCategory.Attribute(CategoryAttrName)?.Value ?? "<no category name>");
                }
                else
                {
                    logger.LogWarning("Unknown element '{Element}'", fieldOrCategory.Name);
                }
            }
        }
        else
        {
            logger.LogError("Unable to parse form definition: {FormDefinition}", formDefinitionXml);
        }
    }

    public string? GetPatched() => xDoc.Root?.ToString();

    public IReadOnlyDictionary<string, string> GetPendingVisibilityConditions() => pendingVisibilityConditions;

    public void PatchField(XElement field)
    {
        var columnAttr = field.Attribute(FieldAttrColumn);
        var systemAttr = field.Attribute(FieldAttrSystem);
        var isPkAttr = field.Attribute(FieldAttrIspk);
        var columnTypeAttr = field.Attribute(FieldAttrColumntype);
        var visibleAttr = field.Attribute(FieldAttrVisible);
        var enabledAttr = field.Attribute(FieldAttrEnabled);
        var guidAttr = field.Attribute(FieldAttrGuid);

        bool isPk = bool.TryParse(isPkAttr?.Value, out bool isPkParsed) && isPkParsed;
        bool system = bool.TryParse(systemAttr?.Value, out bool sysParsed) && sysParsed;

        string fieldDescriptor = (columnAttr ?? guidAttr)?.Value ?? "<no guid or column>";

        if (string.Equals(columnAttr?.Value, "PageInternalRedirectNodeGuid", StringComparison.InvariantCultureIgnoreCase))
        {
            var eventInfo = new EventLogInfo
            {
                Source = "MigrationTool",
                EventCode = "FIELD_BEFORE_PATCH",
                EventDescription = $"Field={columnAttr?.Value}, Visible={visibleAttr?.Value ?? "<null>"}, Enabled={enabledAttr?.Value ?? "<null>"}, ColumnType={columnTypeAttr?.Value ?? "<null>"}",
                EventType = EventTypeEnum.Information.ToString()
            };

            EventLogProvider.LogEvent(eventInfo);
        }

        foreach (var a in field.Attributes().ToList())
        {
            string an = a.Name.ToString();
            if (!allowedFieldAttributes.Contains(an))
            {
                logger.LogTrace(
                    "Removing attribute '{AttributeName}'='{Value}' from field with column '{ColumnName}'",
                    an,
                    a.Value,
                    columnAttr?.Value);

                a.Remove();
            }
        }

        if (discardSysFields && (system || isPk))
        {
            logger.LogDebug("Discard sys filed == true => Removing field sys '{Field}'", fieldDescriptor);
            field.Remove();
            return;
        }

        string? columnType = columnTypeAttr?.Value;
        if (columnType == null)
        {
            if (isPk)
            {
                return;
            }

            logger.LogError("Field ('{Field}') 'columnType' attribute is required", fieldDescriptor);
            return;
        }

        var controlNameElem = field.XPathSelectElement($"{FieldElemSettings}/{SettingsElemControlname}");
        string? controlName = controlNameElem?.Value;

        var fieldMigrationContext = new FieldMigrationContext(
            columnType,
            controlName,
            columnAttr?.Value,
            new EmptySourceObjectContext());

        // Extract before the switch — TfcDirective.Clear calls field.RemoveNodes() which would destroy <visiblemacro>
        string? visibilityConditionJson = ExtractVisibilityConditionXml(field, fieldDescriptor);

        switch (fieldMigrationService.GetFieldMigration(fieldMigrationContext, allowNullSourceFormControl))
        {
            case FieldMigration(_, var targetDataType, _, var targetFormComponent, var actions, _):
            {
                logger.LogDebug(
                    "Field {FieldDescriptor} DataType: {SourceDataType} => {TargetDataType}",
                    fieldDescriptor,
                    columnType,
                    targetDataType);

                if (string.Equals(columnAttr?.Value, "PageInternalRedirectNodeGuid", StringComparison.InvariantCultureIgnoreCase))
                {
                    var eventInfo = new EventLogInfo
                    {
                        Source = "MigrationTool",
                        EventCode = "FIELD_BEFORE_PATCH",
                        EventDescription = $"Field={columnAttr?.Value}, Visible={visibleAttr?.Value ?? "<null>"}, Enabled={enabledAttr?.Value ?? "<null>"}, ColumnType={columnTypeAttr?.Value ?? "<null>"}",
                        EventType = EventTypeEnum.Information.ToString()
                    };

                    EventLogProvider.LogEvent(eventInfo);
                }

                columnTypeAttr?.SetValue(targetDataType);

                // Track integer→text conversions so visibility conditions on other fields
                // that reference this field use string comparison (IsEqualToString) instead of
                // integer comparison (IsEqualToInteger).
                if (string.Equals(columnType, KsFieldDataType.Integer, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(targetDataType, KsFieldDataType.Text, StringComparison.OrdinalIgnoreCase) &&
                    columnAttr?.Value is { } convertedFieldName)
                {
                    integerToTextConvertedFields.Add(convertedFieldName);
                    // Text columns need an explicit size; integer fields have none.
                    if (field.Attribute(FieldAttrColumnsize) == null)
                    {
                        field.SetAttributeValue(FieldAttrColumnsize, "100");
                    }
                    logger.LogDebug("Field '{Field}' converted from integer to text (dropdown control)", fieldDescriptor);
                }

                switch (targetFormComponent)
                {
                    case TfcDirective.DoNothing:
                        logger.LogDebug(
                            "Field {FieldDescriptor} ControlName: Tca:{TcaDirective}",
                            fieldDescriptor,
                            targetFormComponent);
                        PerformActionsOnField(field, fieldDescriptor, actions);
                        break;

                    case TfcDirective.Clear:
                        logger.LogDebug("Field {FieldDescriptor} ControlName: Tca:{TcaDirective}", fieldDescriptor, targetFormComponent);
                        field.RemoveNodes();
                        break;

                    case TfcDirective.CopySourceControl:
                        logger.LogDebug("Field {FieldDescriptor} ControlName: Tca:{TcaDirective} => {ControlName}", fieldDescriptor, targetFormComponent, controlName);
                        controlNameElem?.SetValue(controlName!);
                        PerformActionsOnField(field, fieldDescriptor, actions);
                        break;

                    default:
                    {
                        logger.LogDebug("Field {FieldDescriptor} ControlName: Tca:NONE => from control '{ControlName}' => {TargetFormComponent}", fieldDescriptor, controlName, targetFormComponent);
                        controlNameElem?.SetValue(targetFormComponent!);

                        if (allowNullSourceFormControl)
                        {
                            field.EnsureElement("settings", s =>
                                s.EnsureElement(SettingsElemControlname, cn => cn.Value = targetFormComponent!));

                            field.SetAttributeValue(SettingsElemControlname, targetFormComponent);
                        }

                        PerformActionsOnField(field, fieldDescriptor, actions);
                        break;
                    }
                }

                break;
            }

            case { } fieldMigration when fieldMigration.ShallMigrate(fieldMigrationContext):
            {
                fieldMigration.MigrateFieldDefinition(this, field, columnTypeAttr, fieldDescriptor);
                break;
            }

            default:
                break;
        }

        if (!classIsForm && !classIsDocumentType)
        {
            bool hasVisibleAttribute = visibleAttr != null;

            if (enabledAttr is { } enabled)
            {
                enabled.Remove();
                logger.LogDebug("Removing field '{Field}' attribute '{Attribute}'", fieldDescriptor, FieldAttrEnabled);
            }

            if (system && classIsCustom)
            {
                systemAttr?.Remove();
                logger.LogDebug("Removing field '{Field}' attribute '{Attribute}'", fieldDescriptor, systemAttr?.Name);
            }

            if (hasVisibleAttribute && visibleAttr?.Value is { } visibleValue)
            {
                field.Add(new XAttribute(FieldAttrEnabled, visibleValue));
                logger.LogDebug("Set field '{Field}' attribute '{Attribute}' to value '{Value}' from attribute '{SourceAttribute}'", fieldDescriptor, FieldAttrEnabled, visibleValue, FieldAttrVisible);
            }

            if (!altForm)
            {
                if (hasVisibleAttribute)
                {
                    visibleAttr?.Remove();
                    logger.LogDebug("Removing field '{Field}' attribute '{Attribute}'", fieldDescriptor, FieldAttrVisible);
                }
            }

            foreach (var fieldChildNode in field.Elements().ToList())
            {
                logger.LogDebug("Patching filed child '{FieldChildName}'", fieldChildNode.Name);

                switch (fieldChildNode.Name.ToString())
                {
                    case FieldElemProperties:
                    {
                        PatchProperties(fieldChildNode);
                        break;
                    }
                    case FieldElemSettings:
                    {
                        if (altForm)
                        {
                            PatchSettings(fieldChildNode);
                        }
                        else
                        {
                            ClearSettings(fieldChildNode);
                        }

                        break;
                    }
                    default:
                    {
                        logger.LogDebug("Removing field element '{ElementName}'", fieldChildNode.Name);
                        fieldChildNode.Remove();
                        break;
                    }
                }
            }
        }

        if (classIsForm || classIsDocumentType)
        {
            if (field.Attribute(FieldAttrVisible) is { } visible)
            {
                field.SetAttributeValue(FieldAttrEnabled, visible.Value);
                logger.LogDebug(
                    "Set field '{Field}' attribute '{Attribute}' to value '{Value}' from attribute '{SourceAttribute}'",
                    fieldDescriptor,
                    FieldAttrEnabled,
                    visible.Value,
                    FieldAttrVisible);
            }
        }

        // If the field has a non-empty default value it will always be populated — mark it as optional
        // so the XbyK admin UI does not incorrectly show it as Required.
        if (field.Attribute(FieldAttrAllowEmpty) == null)
        {
            var defaultValueElem = field.XPathSelectElement($"{FieldElemProperties}/{PropertiesElemDefaultvalue}");
            if (defaultValueElem != null && !string.IsNullOrEmpty(defaultValueElem.Value))
            {
                field.SetAttributeValue(FieldAttrAllowEmpty, "true");
                logger.LogDebug("Field '{Field}' has a default value — setting allowempty=true", fieldDescriptor);
            }
        }

        if (visibilityConditionJson != null && columnAttr?.Value is { } condFieldName)
        {
            pendingVisibilityConditions[condFieldName] = visibilityConditionJson;
            logger.LogDebug("Queued visibility condition for field '{Field}'", fieldDescriptor);
        }

        if (string.Equals(columnAttr?.Value, "PageInternalRedirectNodeGuid", StringComparison.InvariantCultureIgnoreCase))
        {
            field.SetAttributeValue(FieldAttrVisible, true);
            field.SetAttributeValue(FieldAttrEnabled, true);

            var eventInfo = new EventLogInfo
            {
                Source = "MigrationTool",
                EventCode = "FIELD_BEFORE_PATCH",
                EventDescription = $"Field={columnAttr?.Value}, Visible={visibleAttr?.Value ?? "<null>"}, Enabled={enabledAttr?.Value ?? "<null>"}, ColumnType={columnTypeAttr?.Value ?? "<null>"}",
                EventType = EventTypeEnum.Information.ToString()
            };

            EventLogProvider.LogEvent(eventInfo);
        }
    }

    private string? ExtractVisibilityConditionXml(XElement field, string fieldDescriptor)
    {
        var propertiesElem = field.Element(FieldElemProperties);
        var visCondElem = propertiesElem?.Element(PropertiesElemVisiblemacro)
            ?? field.Element(PropertiesElemVisiblemacro);

        if (visCondElem == null)
        {
            return null;
        }

        string conditionExpression = visCondElem.Value.Trim();
        visCondElem.Remove();

        if (string.IsNullOrWhiteSpace(conditionExpression))
        {
            return null;
        }

        // Strip Kentico macro wrapper {% ... %} if present
        if (conditionExpression.StartsWith("{%") && conditionExpression.EndsWith("%}"))
        {
            conditionExpression = conditionExpression[2..^2];
            // Remove the security context appended after the first pipe
            int pipeIdx = conditionExpression.IndexOf("|(identity)", StringComparison.OrdinalIgnoreCase);
            if (pipeIdx >= 0)
            {
                conditionExpression = conditionExpression[..pipeIdx];
            }
            conditionExpression = conditionExpression.Trim();
        }

        // Field has a visibility condition — it must not be required, or validation will fire
        // when the field is hidden. Clear allowempty regardless of whether the condition parses.
        field.SetAttributeValue(FieldAttrAllowEmpty, "true");

        string? conditionXml = TryParseSimpleCondition(conditionExpression, integerToTextConvertedFields);
        if (conditionXml == null)
        {
            logger.LogWarning("Field '{Field}': visibility condition '{Condition}' is too complex to migrate automatically — set it manually in the XbyK admin UI", fieldDescriptor, conditionExpression);
        }

        return conditionXml;
    }

    private static string? TryParseSimpleCondition(string condition, IReadOnlySet<string>? intToTextFields = null)
    {
        // FieldName == value  or  FieldName.Value == value  (KX13 uses .Value suffix)
        var eqMatch = Regex.Match(condition, @"^(\w+)(?:\.Value)?\s*(==|!=)\s*(.+)$");
        if (!eqMatch.Success)
        {
            return TryParseMethodCallCondition(condition);
        }

        string fieldName = eqMatch.Groups[1].Value;
        string op = eqMatch.Groups[2].Value;
        string valueStr = eqMatch.Groups[3].Value.Trim();

        // Null / empty string  →  IsEmptyString / IsNotEmptyString
        // Uses VisibilityConditionWithDependencyProperties (PropertyName only).
        if (valueStr is "null" or "\"\"" or "''")
        {
            string id = op == "==" ? "Kentico.Administration.IsEmptyString" : "Kentico.Administration.IsNotEmptyString";
            return BuildVcXmlDependency(id, fieldName);
        }

        // Boolean  →  IsTrueVisibilityCondition / IsFalseVisibilityCondition
        // Uses VisibilityConditionWithDependencyProperties (PropertyName only).
        if (valueStr is "true" or "false")
        {
            if (op != "==")
            {
                return null;
            }

            string id = valueStr == "true"
                ? "Kentico.Administration.IsTrueVisibilityCondition"
                : "Kentico.Administration.IsFalseVisibilityCondition";
            return BuildVcXmlDependency(id, fieldName);
        }

        // Integer literal — if the referenced field was converted from integer to text (e.g. dropdown),
        // use string comparison so the condition matches the stored text value ("0", "1", …).
        if (int.TryParse(valueStr, out int intVal))
        {
            string valStr = intVal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (intToTextFields?.Contains(fieldName) == true)
            {
                string id = op == "==" ? "Kentico.Administration.IsEqualToString" : "Kentico.Administration.NotEqualsString";
                return BuildVcXmlString(id, fieldName, valStr);
            }
            else
            {
                string id = op == "==" ? "Kentico.Administration.IsEqualToInteger" : "Kentico.Administration.IsNotEqualToInteger";
                return BuildVcXmlNumeric(id, fieldName, valStr);
            }
        }

        // Decimal literal
        if (decimal.TryParse(valueStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal decVal))
        {
            string valStr = decVal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (intToTextFields?.Contains(fieldName) == true)
            {
                string id = op == "==" ? "Kentico.Administration.IsEqualToString" : "Kentico.Administration.NotEqualsString";
                return BuildVcXmlString(id, fieldName, valStr);
            }
            else
            {
                string id = op == "==" ? "Kentico.Administration.IsEqualToDecimal" : "Kentico.Administration.IsNotEqualToDecimal";
                return BuildVcXmlNumeric(id, fieldName, valStr);
            }
        }

        // Quoted string — uses StringComparisonConditionProperties (Value + CaseSensitive)
        if ((valueStr.StartsWith('"') && valueStr.EndsWith('"')) ||
            (valueStr.StartsWith('\'') && valueStr.EndsWith('\'')))
        {
            string cleanValue = valueStr[1..^1];
            string id = op == "==" ? "Kentico.Administration.IsEqualToString" : "Kentico.Administration.NotEqualsString";
            return BuildVcXmlString(id, fieldName, cleanValue);
        }

        return null;
    }

    private static string? TryParseMethodCallCondition(string condition)
    {
        // FieldName.Contains("value")  /  StartsWith  /  EndsWith  /  !FieldName.Contains(...)
        // All use StringComparisonConditionProperties (Value + CaseSensitive).
        bool negated = condition.StartsWith('!');
        string expr = negated ? condition[1..].Trim() : condition;

        var methodMatch = Regex.Match(expr, @"^(\w+)(?:\.Value)?\.(Contains|StartsWith|EndsWith)\(\s*""(.*?)""\s*\)$");
        if (!methodMatch.Success)
        {
            return null;
        }

        string fieldName = methodMatch.Groups[1].Value;
        string method = methodMatch.Groups[2].Value;
        string value = methodMatch.Groups[3].Value;

        string id = method switch
        {
            "Contains" => negated ? "Kentico.Administration.NotContainsString" : "Kentico.Administration.ContainsString",
            "StartsWith" => "Kentico.Administration.StartsWithString",
            "EndsWith" => "Kentico.Administration.EndsWithString",
            _ => null!
        };

        if (negated && method is "StartsWith" or "EndsWith")
        {
            return null;
        }

        return BuildVcXmlString(id, fieldName, value);
    }

    // For string conditions (IsEqualToString, NotEqualsString, ContainsString, etc.)
    // Uses StringComparisonConditionProperties: PropertyName + Value + CaseSensitive.
    private static string BuildVcXmlString(string conditionId, string propertyName, string value) =>
        $"<VisibilityConditionConfiguration>" +
        $"<Identifier>{System.Security.SecurityElement.Escape(conditionId)}</Identifier>" +
        $"<Properties>" +
        $"<PropertyName>{System.Security.SecurityElement.Escape(propertyName)}</PropertyName>" +
        $"<Value>{System.Security.SecurityElement.Escape(value)}</Value>" +
        $"<CaseSensitive>true</CaseSensitive>" +
        $"</Properties>" +
        $"</VisibilityConditionConfiguration>";

    // For integer/decimal conditions (IsEqualToInteger, IsEqualToDecimal, etc.)
    // Uses a simple Value element — same pattern as string conditions but without CaseSensitive.
    private static string BuildVcXmlNumeric(string conditionId, string propertyName, string value) =>
        $"<VisibilityConditionConfiguration>" +
        $"<Identifier>{System.Security.SecurityElement.Escape(conditionId)}</Identifier>" +
        $"<Properties>" +
        $"<PropertyName>{System.Security.SecurityElement.Escape(propertyName)}</PropertyName>" +
        $"<Value>{System.Security.SecurityElement.Escape(value)}</Value>" +
        $"</Properties>" +
        $"</VisibilityConditionConfiguration>";

    // For dependency-only conditions (IsEmptyString, IsNotEmptyString, IsTrueVisibilityCondition, IsFalseVisibilityCondition).
    // Uses VisibilityConditionWithDependencyProperties: PropertyName only.
    private static string BuildVcXmlDependency(string conditionId, string propertyName) =>
        $"<VisibilityConditionConfiguration>" +
        $"<Identifier>{System.Security.SecurityElement.Escape(conditionId)}</Identifier>" +
        $"<Properties>" +
        $"<PropertyName>{System.Security.SecurityElement.Escape(propertyName)}</PropertyName>" +
        $"</Properties>" +
        $"</VisibilityConditionConfiguration>";

    private void ClearSettings(XElement settingsElem)
    {
        var elementsToRemove = settingsElem.Elements().ToList();

        foreach (var element in elementsToRemove)
        {
            logger.LogDebug("Removing settings element '{ElementName}'", element.Name);
            element.Remove();
        }

        if (!settingsElem.Elements().Any())
        {
            settingsElem.Remove();
        }
    }

    private void PatchSettings(XElement settingsElem)
    {
        var elementsToRemove = settingsElem.Elements()
            .Where(element => element.Name != SettingsElemControlname)
            .ToList();

        foreach (var element in elementsToRemove)
        {
            logger.LogDebug("Removing settings element '{ElementName}'", element.Name);
            element.Remove();
        }

        if (!settingsElem.Elements().Any())
        {
            settingsElem.Remove();
        }
    }

    private void PatchProperties(XElement properties)
    {
        var elementsToRemove = properties.Elements()
            .Where(element => !new string[] { PropertiesElemDefaultvalue, PropertiesElemFieldcaption }.Any(x => element.Name == x))
            .ToList();

        foreach (var element in elementsToRemove)
        {
            logger.LogDebug("Removing properties element '{ElementName}'", element.Name);
            element.Remove();
        }

        if (!properties.Elements().Any())
        {
            logger.LogDebug("Properties element is empty => removing");
            properties.Remove();
        }
    }

    private void PerformActionsOnField(XElement field, string fieldDescriptor, string[]? actions)
    {
        if (actions == null)
        {
            return;
        }

        foreach (string action in actions)
        {
            logger.LogDebug("Field {FieldDescriptor} Action: {Action}", fieldDescriptor, action);

            switch (action)
            {
                case TcaDirective.ClearSettings:
                {
                    field.Element(FieldElemSettings)?.Remove();
                    break;
                }
                case TcaDirective.ClearMacroTable:
                {
                    break;
                }
                case TcaDirective.ConvertToAsset:
                {
                    field
                        .EnsureElement(FieldElemSettings)
                        .EnsureElement(SettingsMaximumassets, maxAssets => maxAssets.Value = SettingsMaximumassetsFallback);
                    break;
                }
                case TcaDirective.ConvertToPages:
                {
                    field.EnsureElement(FieldElemSettings, settings =>
                    {
                        settings.EnsureElement(SettingsMaximumpages, maxAssets => maxAssets.Value = SettingsMaximumpagesFallback);
                        settings.EnsureElement(SettingsRootpath, maxAssets => maxAssets.Value = SettingsRootpathFallback);
                    });

                    field.SetAttributeValue(FieldAttrSize, FieldAttrSizeZero);

                    var settings = field.EnsureElement(FieldElemSettings);
                    settings.EnsureElement("TreePath", element => element.Value = settings.Element("RootPath")?.Value ?? "");
                    settings.EnsureElement("RootPath").Remove();

                    break;
                }
                case TcaDirective.ConvertToRichText:
                {
                    field
                        .EnsureElement(FieldElemSettings, settings =>
                            settings.EnsureElement("ConfigurationName", e => e.Value = "Kentico.Administration.StructuredContent"));
                    break;
                }
                default:
                {
                    break;
                }
            }
        }
    }
}
