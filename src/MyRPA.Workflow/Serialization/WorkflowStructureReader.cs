using System.Globalization;
using System.Text.Json;
using MyRPA.Workflow.Validation;

namespace MyRPA.Workflow.Serialization;

/// <summary>
/// Stage 2 of the pipeline (ADR-0011): reads the JSON shape into <see cref="RawWorkflow"/>, reporting missing
/// fields, wrong JSON types and unknown fields. Never throws for bad input; continues to collect every problem.
/// </summary>
internal sealed class WorkflowStructureReader
{
    private static readonly string[] _workflowFields = ["schemaVersion", "id", "name", "version", "description", "arguments", "variables", "root"];
    private static readonly string[] _argumentFields = ["name", "direction", "type", "required", "default"];
    private static readonly string[] _variableFields = ["name", "type", "default"];
    private static readonly string[] _nodeFields = ["id", "type", "displayName", "properties", "children", "slots"];

    private readonly List<ValidationDiagnostic> _diagnostics;

    public WorkflowStructureReader(List<ValidationDiagnostic> diagnostics) => _diagnostics = diagnostics;

    public RawWorkflow? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            Error(DiagnosticCodes.RootNotObject, "$", $"The workflow must be a JSON object, not {Describe(root)}.");
            return null;
        }

        WarnUnknownFields(root, "$", _workflowFields);
        var workflow = new RawWorkflow
        {
            SchemaVersion = ReadString(root, "$", "schemaVersion", required: true),
            Id = ReadString(root, "$", "id", required: true),
            Name = ReadString(root, "$", "name", required: true),
            Version = ReadString(root, "$", "version", required: true),
            Description = ReadString(root, "$", "description", required: false),
        };

        foreach (var (element, path) in ReadArray(root, "$", "arguments"))
        {
            if (Expect(element, path, JsonValueKind.Object))
            {
                WarnUnknownFields(element, path, _argumentFields);
                workflow.Arguments.Add(new RawArgument(path)
                {
                    Name = ReadString(element, path, "name", required: true),
                    Direction = ReadString(element, path, "direction", required: true),
                    Type = ReadString(element, path, "type", required: true),
                    Required = ReadBoolean(element, path, "required") ?? false,
                    Default = element.TryGetProperty("default", out var d) ? d : null,
                });
            }
        }

        foreach (var (element, path) in ReadArray(root, "$", "variables"))
        {
            if (Expect(element, path, JsonValueKind.Object))
            {
                WarnUnknownFields(element, path, _variableFields);
                workflow.Variables.Add(new RawVariable(path)
                {
                    Name = ReadString(element, path, "name", required: true),
                    Type = ReadString(element, path, "type", required: true),
                    Default = element.TryGetProperty("default", out var d) ? d : null,
                });
            }
        }

        if (!root.TryGetProperty("root", out var rootNode))
        {
            Error(DiagnosticCodes.MissingField, "$.root", "Required field 'root' is missing.");
        }
        else if (Expect(rootNode, "$.root", JsonValueKind.Object))
        {
            workflow.Root = ReadNode(rootNode, "$.root");
        }

        return workflow;
    }

    private static string Describe(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "null",
    };

    private static string Expected(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        _ => kind.ToString(),
    };

    private RawNode ReadNode(JsonElement element, string path)
    {
        WarnUnknownFields(element, path, _nodeFields);
        var node = new RawNode(path)
        {
            Id = ReadString(element, path, "id", required: true),
            Type = ReadString(element, path, "type", required: true),
            DisplayName = ReadString(element, path, "displayName", required: false),
        };

        if (element.TryGetProperty("properties", out var properties) && Expect(properties, path + ".properties", JsonValueKind.Object))
        {
            foreach (var property in properties.EnumerateObject())
            {
                node.Properties.Add(new RawProperty(property.Name, property.Value, $"{path}.properties.{property.Name}"));
            }
        }

        foreach (var (child, childPath) in ReadArray(element, path, "children"))
        {
            if (Expect(child, childPath, JsonValueKind.Object))
            {
                node.Children.Add(ReadNode(child, childPath));
            }
        }

        if (element.TryGetProperty("slots", out var slots) && Expect(slots, path + ".slots", JsonValueKind.Object))
        {
            foreach (var slot in slots.EnumerateObject())
            {
                var slotPath = $"{path}.slots.{slot.Name}";
                if (Expect(slot.Value, slotPath, JsonValueKind.Object))
                {
                    node.Slots.Add(new(slot.Name, ReadNode(slot.Value, slotPath)));
                }
            }
        }

        return node;
    }

    private string? ReadString(JsonElement element, string path, string field, bool required)
    {
        if (!element.TryGetProperty(field, out var value))
        {
            if (required)
            {
                Error(DiagnosticCodes.MissingField, $"{path}.{field}", $"Required field '{field}' is missing.");
            }

            return null;
        }

        return Expect(value, $"{path}.{field}", JsonValueKind.String) ? value.GetString() : null;
    }

    private bool? ReadBoolean(JsonElement element, string path, string field)
    {
        if (!element.TryGetProperty(field, out var value))
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        Error(DiagnosticCodes.WrongFieldType, $"{path}.{field}", $"Expected a boolean but found {Describe(value)}.");
        return null;
    }

    private IEnumerable<(JsonElement Element, string Path)> ReadArray(JsonElement element, string path, string field)
    {
        if (!element.TryGetProperty(field, out var array) || !Expect(array, $"{path}.{field}", JsonValueKind.Array))
        {
            yield break;
        }

        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            yield return (item, string.Create(CultureInfo.InvariantCulture, $"{path}.{field}[{index++}]"));
        }
    }

    private bool Expect(JsonElement element, string path, JsonValueKind kind)
    {
        if (element.ValueKind == kind)
        {
            return true;
        }

        Error(DiagnosticCodes.WrongFieldType, path, $"Expected {Expected(kind)} but found {Describe(element)}.");
        return false;
    }

    private void WarnUnknownFields(JsonElement element, string path, string[] known)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (Array.IndexOf(known, property.Name) < 0)
            {
                _diagnostics.Add(new ValidationDiagnostic(
                    DiagnosticCodes.UnknownField,
                    DiagnosticSeverity.Warning,
                    $"Unknown field '{property.Name}' is ignored.",
                    $"{path}.{property.Name}"));
            }
        }
    }

    private void Error(string code, string path, string message) =>
        _diagnostics.Add(new ValidationDiagnostic(code, DiagnosticSeverity.Error, message, path));
}
