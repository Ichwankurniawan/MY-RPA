using System.Text.Json;
using MyRPA.Core.Activities;
using MyRPA.Core.Identifiers;
using MyRPA.Workflow.Expressions;
using MyRPA.Workflow.Serialization;
using MyRPA.Workflow.Values;

namespace MyRPA.Workflow.Validation;

/// <summary>
/// Stage 4 of the pipeline (ADR-0011): checks identity, arguments, variables, node ids, activity types, properties,
/// expressions, name references, assignment targets, children and slots against the activity catalog. Collects all
/// diagnostics; builds a <see cref="WorkflowDefinition"/> only when there are no errors.
/// </summary>
internal sealed class WorkflowSemanticValidator(IActivityCatalog catalog, List<ValidationDiagnostic> diagnostics)
{
    private readonly HashSet<string> _nodeIds = new(StringComparer.Ordinal);

    private enum SymbolKind
    {
        InArgument,
        OutArgument,
        InOutArgument,
        Variable,
        Local,
    }

    public WorkflowDefinition? Validate(RawWorkflow raw, WorkflowSchemaVersion schemaVersion)
    {
        var errorsBefore = ErrorCount();

        WorkflowId? id = null;
        if (raw.Id is not null && !WorkflowId.TryCreate(raw.Id, out id))
        {
            Error(DiagnosticCodes.InvalidWorkflowId, "$.id", $"'{raw.Id}' is not a valid workflow id (1-{Identifier.MaxLength} characters from [A-Za-z0-9-_.:]).");
        }

        RequireText(raw.Name, "$.name", "name");
        RequireText(raw.Version, "$.version", "version");

        var scope = new Scope(null);
        var arguments = ValidateArguments(raw.Arguments, scope);
        var variables = ValidateVariables(raw.Variables, scope);
        var root = raw.Root is null ? null : ValidateNode(raw.Root, scope);

        if (ErrorCount() > errorsBefore || id is null || root is null || string.IsNullOrWhiteSpace(raw.Name) || string.IsNullOrWhiteSpace(raw.Version))
        {
            return null;
        }

        return new WorkflowDefinition(id, raw.Name, raw.Version, root, schemaVersion, arguments!, variables!, raw.Description);
    }

    private static bool IsWritable(SymbolKind kind) => kind is SymbolKind.Variable or SymbolKind.OutArgument or SymbolKind.InOutArgument;

    private static bool TryParseDirection(string? text, out ArgumentDirection direction)
    {
        direction = default;
        return text is "In" or "Out" or "InOut" && Enum.TryParse(text, out direction);
    }

    private List<ArgumentDefinition?> ValidateArguments(List<RawArgument> rawArguments, Scope scope)
    {
        var result = new List<ArgumentDefinition?>();
        foreach (var raw in rawArguments)
        {
            var ok = ValidateName(raw.Name, raw.Path, scope, out var name);

            var hasDirection = TryParseDirection(raw.Direction, out var direction);
            if (raw.Direction is not null && !hasDirection)
            {
                Error(DiagnosticCodes.InvalidDirection, raw.Path + ".direction", $"'{raw.Direction}' is not a direction. Use In, Out or InOut.");
            }

            var hasType = ValidateType(raw.Type, raw.Path, out var type);
            ok &= hasDirection && hasType;

            if (hasDirection && direction == ArgumentDirection.Out && (raw.Required || raw.Default is not null))
            {
                Error(DiagnosticCodes.OutArgumentMisuse, raw.Path, "Out arguments cannot be required or have a default value.");
                ok = false;
            }

            object? defaultValue = null;
            if (raw.Default is { } defaultElement && hasType && ok)
            {
                ok &= ValidateDefault(defaultElement, type, raw.Path, out defaultValue);
            }

            if (name is not null && hasDirection)
            {
                scope.Declare(name, direction switch
                {
                    ArgumentDirection.In => SymbolKind.InArgument,
                    ArgumentDirection.Out => SymbolKind.OutArgument,
                    _ => SymbolKind.InOutArgument,
                });
            }

            result.Add(ok ? new ArgumentDefinition(name!, direction, type, raw.Required, defaultValue) : null);
        }

        return result;
    }

    private List<VariableDefinition?> ValidateVariables(List<RawVariable> rawVariables, Scope scope)
    {
        var result = new List<VariableDefinition?>();
        foreach (var raw in rawVariables)
        {
            var ok = ValidateName(raw.Name, raw.Path, scope, out var name);
            var hasType = ValidateType(raw.Type, raw.Path, out var type);
            ok &= hasType;

            object? defaultValue = null;
            if (raw.Default is { } defaultElement && hasType)
            {
                ok &= ValidateDefault(defaultElement, type, raw.Path, out defaultValue);
            }

            if (name is not null)
            {
                scope.Declare(name, SymbolKind.Variable);
            }

            result.Add(ok ? new VariableDefinition(name!, type, defaultValue) : null);
        }

        return result;
    }

    private bool ValidateName(string? name, string path, Scope scope, out string? validName)
    {
        validName = null;
        if (name is null)
        {
            return false;
        }

        if (!WorkflowNames.IsValid(name))
        {
            Error(DiagnosticCodes.InvalidName, path + ".name", $"'{name}' is not a valid name (letters, digits, '_'; must not start with a digit).");
            return false;
        }

        if (scope.TryFind(name, out _))
        {
            Error(DiagnosticCodes.DuplicateName, path + ".name", $"Name '{name}' is declared more than once.");
            return false;
        }

        validName = name;
        return true;
    }

    private bool ValidateType(string? text, string path, out WorkflowDataType type)
    {
        type = default;
        if (text is null)
        {
            return false;
        }

        if (!WorkflowValues.TryParseDataType(text, out type))
        {
            Error(DiagnosticCodes.InvalidDataType, path + ".type", $"'{text}' is not a data type. Use {string.Join(", ", Enum.GetNames<WorkflowDataType>())}.");
            return false;
        }

        return true;
    }

    private bool ValidateDefault(JsonElement element, WorkflowDataType type, string path, out object? value)
    {
        if (WorkflowValues.TryFromJson(element, type, out value, out var error))
        {
            return true;
        }

        Error(DiagnosticCodes.InvalidDefault, path + ".default", $"Default value does not match type {type}: {error}");
        return false;
    }

    private NodeDefinition? ValidateNode(RawNode raw, Scope scope)
    {
        var errorsBefore = ErrorCount();

        NodeId? id = null;
        if (raw.Id is not null)
        {
            if (!NodeId.TryCreate(raw.Id, out id))
            {
                Error(DiagnosticCodes.InvalidNodeId, raw.Path + ".id", $"'{raw.Id}' is not a valid node id (1-{Identifier.MaxLength} characters from [A-Za-z0-9-_.:]).");
            }
            else if (!_nodeIds.Add(id.Value))
            {
                Error(DiagnosticCodes.DuplicateNodeId, raw.Path + ".id", $"Node id '{id}' is used more than once.", id.Value);
            }
        }

        var nodeId = id?.Value;
        ActivityTypeName? type = null;
        ActivityDescriptor? descriptor = null;
        if (raw.Type is not null)
        {
            if (!ActivityTypeName.TryCreate(raw.Type, out type))
            {
                Error(DiagnosticCodes.InvalidActivityType, raw.Path + ".type", $"'{raw.Type}' is not a valid activity type name (expected 'Namespace.Name').", nodeId);
            }
            else if (!catalog.TryGet(type, out descriptor))
            {
                Error(DiagnosticCodes.UnknownActivityType, raw.Path + ".type", $"Activity type '{type}' is not registered.", nodeId);
            }
        }

        var properties = new List<KeyValuePair<string, PropertyValue>>();
        var locals = new List<(string Name, IReadOnlyList<string> Slots)>();
        if (descriptor is not null)
        {
            ValidateProperties(raw, descriptor, scope, nodeId, properties, locals);
            ValidateStructure(raw, descriptor, nodeId);
        }

        var children = raw.Children.Select(child => ValidateNode(child, scope)).ToList();
        var slots = new List<KeyValuePair<string, NodeDefinition?>>();
        foreach (var (slotName, slotNode) in raw.Slots)
        {
            var slotLocals = locals.Where(l => l.Slots.Contains(slotName, StringComparer.Ordinal)).ToList();
            var slotScope = scope;
            if (slotLocals.Count > 0)
            {
                slotScope = new Scope(scope);
                foreach (var local in slotLocals)
                {
                    slotScope.Declare(local.Name, SymbolKind.Local);
                }
            }

            slots.Add(new(slotName, ValidateNode(slotNode, slotScope)));
        }

        if (ErrorCount() > errorsBefore || id is null || type is null)
        {
            return null;
        }

        return new NodeDefinition(id, type, raw.DisplayName, children!, properties, slots!);
    }

    private void ValidateProperties(
        RawNode raw,
        ActivityDescriptor descriptor,
        Scope scope,
        string? nodeId,
        List<KeyValuePair<string, PropertyValue>> properties,
        List<(string Name, IReadOnlyList<string> Slots)> locals)
    {
        foreach (var rawProperty in raw.Properties)
        {
            var definition = descriptor.FindProperty(rawProperty.Name);
            if (definition is null)
            {
                var known = descriptor.Properties.Count == 0 ? "none" : string.Join(", ", descriptor.Properties.Select(p => p.Name));
                Error(DiagnosticCodes.UnknownProperty, rawProperty.Path, $"Activity '{descriptor.TypeName}' has no property '{rawProperty.Name}' (known: {known}).", nodeId);
                continue;
            }

            var value = ValidateProperty(definition, rawProperty, scope, nodeId);
            if (value is null)
            {
                continue;
            }

            properties.Add(new(rawProperty.Name, value));
            if (definition.Kind == ActivityPropertyKind.LocalName && value is NamePropertyValue local)
            {
                locals.Add((local.Name, definition.ScopeSlots));
            }
        }

        foreach (var required in descriptor.Properties.Where(p => p.IsRequired))
        {
            if (!raw.Properties.Any(p => string.Equals(p.Name, required.Name, StringComparison.Ordinal)))
            {
                Error(DiagnosticCodes.MissingProperty, raw.Path + ".properties", $"Required property '{required.Name}' of '{descriptor.TypeName}' is missing.", nodeId);
            }
        }
    }

    private void ValidateStructure(RawNode raw, ActivityDescriptor descriptor, string? nodeId)
    {
        if (!descriptor.AllowsChildren && raw.Children.Count > 0)
        {
            Error(DiagnosticCodes.ChildrenNotAllowed, raw.Path + ".children", $"Activity '{descriptor.TypeName}' does not accept a children list.", nodeId);
        }

        foreach (var (slotName, slotNode) in raw.Slots)
        {
            if (descriptor.FindSlot(slotName) is null)
            {
                var known = descriptor.Slots.Count == 0
                    ? "none"
                    : string.Join(", ", descriptor.Slots.Select(s => s.IsPrefix ? s.Name + "<value>" : s.Name));
                Error(DiagnosticCodes.UnknownSlot, slotNode.Path, $"Activity '{descriptor.TypeName}' has no slot '{slotName}' (known: {known}).", nodeId);
            }
        }

        foreach (var required in descriptor.Slots.Where(s => s.IsRequired))
        {
            if (!raw.Slots.Any(s => string.Equals(s.Key, required.Name, StringComparison.Ordinal)))
            {
                Error(DiagnosticCodes.MissingSlot, raw.Path + ".slots", $"Required slot '{required.Name}' of '{descriptor.TypeName}' is missing.", nodeId);
            }
        }
    }

    private PropertyValue? ValidateProperty(ActivityPropertyDefinition definition, RawProperty raw, Scope scope, string? nodeId)
    {
        switch (definition.Kind)
        {
            case ActivityPropertyKind.Expression:
                return ParseExpression(raw.Value, raw.Path, scope, nodeId) is { } expression ? new ExpressionPropertyValue(expression) : null;

            case ActivityPropertyKind.Text:
                if (raw.Value.ValueKind != JsonValueKind.String)
                {
                    Error(DiagnosticCodes.InvalidPropertyValue, raw.Path, "Expected a text value (JSON string).", nodeId);
                    return null;
                }

                var text = raw.Value.GetString()!;
                if (definition.AllowedValues.Count > 0 && !definition.AllowedValues.Contains(text, StringComparer.Ordinal))
                {
                    Error(DiagnosticCodes.ValueNotAllowed, raw.Path, $"'{text}' is not allowed. Use one of: {string.Join(", ", definition.AllowedValues)}.", nodeId);
                    return null;
                }

                return new TextPropertyValue(text);

            case ActivityPropertyKind.AssignmentTarget:
                return ReadName(raw.Value, raw.Path, nodeId) is { } target && CheckAssignmentTarget(target, raw.Path, scope, nodeId)
                    ? new NamePropertyValue(target)
                    : null;

            case ActivityPropertyKind.LocalName:
                var local = ReadName(raw.Value, raw.Path, nodeId);
                if (local is null)
                {
                    return null;
                }

                if (scope.TryFind(local, out _))
                {
                    Error(DiagnosticCodes.ShadowedName, raw.Path, $"Local '{local}' hides an existing name; choose a different name.", nodeId);
                    return null;
                }

                return new NamePropertyValue(local);

            case ActivityPropertyKind.ExpressionMap:
                return ValidateMap(raw, nodeId, (element, path) => ParseExpression(element, path, scope, nodeId)) is { } expressions
                    ? new ExpressionMapPropertyValue(expressions)
                    : null;

            case ActivityPropertyKind.AssignmentTargetMap:
                return ValidateMap(raw, nodeId, (element, path) =>
                        ReadName(element, path, nodeId) is { } name && CheckAssignmentTarget(name, path, scope, nodeId) ? name : null) is { } targets
                    ? new NameMapPropertyValue(targets)
                    : null;

            default:
                Error(DiagnosticCodes.InvalidPropertyValue, raw.Path, $"Unsupported property kind {definition.Kind}.", nodeId);
                return null;
        }
    }

    private List<KeyValuePair<string, T>>? ValidateMap<T>(RawProperty raw, string? nodeId, Func<JsonElement, string, T?> readValue)
        where T : class
    {
        if (raw.Value.ValueKind != JsonValueKind.Object)
        {
            Error(DiagnosticCodes.InvalidPropertyValue, raw.Path, "Expected a JSON object mapping names to values.", nodeId);
            return null;
        }

        var ok = true;
        var entries = new List<KeyValuePair<string, T>>();
        foreach (var entry in raw.Value.EnumerateObject())
        {
            var path = $"{raw.Path}.{entry.Name}";
            if (!WorkflowNames.IsValid(entry.Name))
            {
                Error(DiagnosticCodes.InvalidPropertyValue, path, $"'{entry.Name}' is not a valid argument name.", nodeId);
                ok = false;
                continue;
            }

            if (readValue(entry.Value, path) is { } value)
            {
                entries.Add(new(entry.Name, value));
            }
            else
            {
                ok = false;
            }
        }

        return ok ? entries : null;
    }

    private string? ReadName(JsonElement value, string path, string? nodeId)
    {
        if (value.ValueKind == JsonValueKind.String && WorkflowNames.IsValid(value.GetString()))
        {
            return value.GetString();
        }

        Error(DiagnosticCodes.InvalidPropertyValue, path, "Expected a variable or argument name (JSON string).", nodeId);
        return null;
    }

    private bool CheckAssignmentTarget(string name, string path, Scope scope, string? nodeId)
    {
        if (!scope.TryFind(name, out var kind))
        {
            Error(DiagnosticCodes.InvalidAssignmentTarget, path, $"Unknown variable or argument '{name}'.", nodeId);
            return false;
        }

        if (!IsWritable(kind))
        {
            Error(DiagnosticCodes.InvalidAssignmentTarget, path, $"'{name}' is read-only (In argument or local) and cannot be assigned.", nodeId);
            return false;
        }

        return true;
    }

    private WorkflowExpression? ParseExpression(JsonElement value, string path, Scope scope, string? nodeId)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                if (!WorkflowExpression.TryParse(value.GetString()!, out var expression, out var syntaxError))
                {
                    Error(DiagnosticCodes.InvalidExpression, path, syntaxError.Message, nodeId);
                    return null;
                }

                if (expression.FindFunctionProblem() is { } functionProblem)
                {
                    Error(DiagnosticCodes.InvalidExpression, path, functionProblem, nodeId);
                    return null;
                }

                var ok = true;
                foreach (var name in expression.ReferencedNames)
                {
                    if (!scope.TryFind(name, out _))
                    {
                        Error(DiagnosticCodes.UnknownName, path, $"Unknown name '{name}' in expression \"{expression.Source}\".", nodeId);
                        ok = false;
                    }
                }

                return ok ? expression : null;

            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null:
                if (!WorkflowValues.TryFromJsonUntyped(value, out var literal, out var literalError))
                {
                    Error(DiagnosticCodes.InvalidPropertyValue, path, literalError, nodeId);
                    return null;
                }

                return WorkflowExpression.Literal(literal, value.GetRawText());

            default:
                Error(DiagnosticCodes.InvalidPropertyValue, path, "Expected an expression (JSON string) or a literal number, boolean or null.", nodeId);
                return null;
        }
    }

    private void RequireText(string? value, string path, string field)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            Error(DiagnosticCodes.InvalidWorkflowText, path, $"Workflow {field} must not be blank.");
        }
    }

    private int ErrorCount() => diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);

    private void Error(string code, string path, string message, string? nodeId = null) =>
        diagnostics.Add(new ValidationDiagnostic(code, DiagnosticSeverity.Error, message, path, nodeId));

    private sealed class Scope(Scope? parent)
    {
        private readonly Dictionary<string, SymbolKind> _symbols = new(StringComparer.Ordinal);

        public void Declare(string name, SymbolKind kind) => _symbols.TryAdd(name, kind);

        public bool TryFind(string name, out SymbolKind kind)
        {
            for (var scope = this; scope is not null; scope = scope.Parent)
            {
                if (scope._symbols.TryGetValue(name, out kind))
                {
                    return true;
                }
            }

            kind = default;
            return false;
        }

        private Scope? Parent { get; } = parent;
    }
}
