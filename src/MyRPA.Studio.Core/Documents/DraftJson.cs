using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MyRPA.Studio.Documents;

/// <summary>
/// Reads and writes <see cref="WorkflowDraft"/>s in the workflow JSON format v1.0 (the same format the CLI and engine
/// use; ADR-0011). Reading accepts every file whose JSON shape can be represented — semantic problems (unknown activity
/// types, invalid expressions, bad names) are kept so they can be fixed in the designer. Writing reports the JSON path of
/// every node, which is how validation diagnostics are mapped back to nodes.
/// </summary>
public static class DraftJson
{
    private static readonly JsonDocumentOptions _readOptions = new() { MaxDepth = 128, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly JsonWriterOptions _writeOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly HashSet<string> _workflowFields = new(StringComparer.Ordinal) { "schemaVersion", "id", "name", "version", "description", "arguments", "variables", "root" };

    private static readonly HashSet<string> _nodeFields = new(StringComparer.Ordinal) { "id", "type", "displayName", "properties", "children", "slots" };

    /// <summary>Reads a draft.</summary>
    /// <param name="json">Workflow JSON.</param>
    /// <exception cref="DraftReadException">The JSON is invalid or has a shape a draft cannot represent.</exception>
    public static WorkflowDraft Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, _readOptions);
        }
        catch (JsonException ex)
        {
            throw new DraftReadException($"The file is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var root = Expect(document.RootElement, "$", JsonValueKind.Object);
            if (!root.TryGetProperty("root", out var rootNode))
            {
                throw new DraftReadException("$.root: the workflow has no root node.");
            }

            return new WorkflowDraft(ReadNode(rootNode, "$.root"))
            {
                SchemaVersion = String(root, "schemaVersion", "$") ?? string.Empty,
                Id = String(root, "id", "$") ?? string.Empty,
                Name = String(root, "name", "$") ?? string.Empty,
                Version = String(root, "version", "$") ?? string.Empty,
                Description = String(root, "description", "$"),
                Arguments = ReadList(root, "arguments", "$", ReadArgument),
                Variables = ReadList(root, "variables", "$", ReadVariable),
                Extra = Extra(root, _workflowFields),
            };
        }
    }

    /// <summary>Writes a draft.</summary>
    /// <param name="draft">The draft.</param>
    public static string Write(WorkflowDraft draft) => Write(draft, out _);

    /// <summary>Writes a draft and returns the JSON path of every node.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="nodePaths">JSON path (e.g. <c>$.root.children[0]</c>) to node path.</param>
    public static string Write(WorkflowDraft draft, out IReadOnlyDictionary<string, NodePath> nodePaths)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var paths = new Dictionary<string, NodePath>(StringComparer.Ordinal);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, _writeOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", draft.SchemaVersion);
            writer.WriteString("id", draft.Id);
            writer.WriteString("name", draft.Name);
            writer.WriteString("version", draft.Version);
            if (draft.Description is not null)
            {
                writer.WriteString("description", draft.Description);
            }

            if (draft.Arguments.Count > 0)
            {
                writer.WriteStartArray("arguments");
                foreach (var argument in draft.Arguments)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", argument.Name);
                    writer.WriteString("direction", argument.Direction);
                    writer.WriteString("type", argument.Type);
                    if (argument.Required)
                    {
                        writer.WriteBoolean("required", true);
                    }

                    WriteDefault(writer, argument.DefaultJson);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            if (draft.Variables.Count > 0)
            {
                writer.WriteStartArray("variables");
                foreach (var variable in draft.Variables)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", variable.Name);
                    writer.WriteString("type", variable.Type);
                    WriteDefault(writer, variable.DefaultJson);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WritePropertyName("root");
            WriteNode(writer, draft.Root, "$.root", NodePath.Root, paths);
            WriteExtra(writer, draft.Extra);
            writer.WriteEndObject();
        }

        nodePaths = paths;
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Writes one node (and its subtree) as a JSON object.</summary>
    /// <param name="node">The node.</param>
    public static string WriteNode(NodeDraft node)
    {
        ArgumentNullException.ThrowIfNull(node);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, _writeOptions))
        {
            WriteNode(writer, node, "$", NodePath.Root, new Dictionary<string, NodePath>(StringComparer.Ordinal));
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Reads one node (and its subtree) from a JSON object.</summary>
    /// <param name="element">The JSON object.</param>
    /// <param name="path">JSON path for error messages.</param>
    public static NodeDraft ReadNode(JsonElement element, string path)
    {
        Expect(element, path, JsonValueKind.Object);
        var node = new NodeDraft(String(element, "id", path) ?? string.Empty, String(element, "type", path) ?? string.Empty)
        {
            DisplayName = String(element, "displayName", path),
            Extra = Extra(element, _nodeFields),
        };

        if (element.TryGetProperty("properties", out var properties))
        {
            var entries = ImmutableList.CreateBuilder<PropertyEntry>();
            foreach (var property in Expect(properties, path + ".properties", JsonValueKind.Object).EnumerateObject())
            {
                entries.Add(new PropertyEntry(property.Name, ReadProperty(property.Value, $"{path}.properties.{property.Name}")));
            }

            node = node with { Properties = entries.ToImmutable() };
        }

        if (element.TryGetProperty("children", out var children))
        {
            var list = ImmutableList.CreateBuilder<NodeDraft>();
            var index = 0;
            foreach (var child in Expect(children, path + ".children", JsonValueKind.Array).EnumerateArray())
            {
                list.Add(ReadNode(child, string.Create(CultureInfo.InvariantCulture, $"{path}.children[{index++}]")));
            }

            node = node with { Children = list.ToImmutable() };
        }

        if (element.TryGetProperty("slots", out var slots))
        {
            var list = ImmutableList.CreateBuilder<SlotEntry>();
            foreach (var slot in Expect(slots, path + ".slots", JsonValueKind.Object).EnumerateObject())
            {
                list.Add(new SlotEntry(slot.Name, ReadNode(slot.Value, $"{path}.slots.{slot.Name}")));
            }

            node = node with { Slots = list.ToImmutable() };
        }

        return node;
    }

    /// <summary>Returns <see langword="true"/> when <paramref name="json"/> is a single valid JSON value.</summary>
    /// <param name="json">Candidate raw JSON.</param>
    public static bool IsJsonValue(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void WriteNode(Utf8JsonWriter writer, NodeDraft node, string jsonPath, NodePath path, Dictionary<string, NodePath> paths)
    {
        paths[jsonPath] = path;
        writer.WriteStartObject();
        writer.WriteString("id", node.Id);
        writer.WriteString("type", node.Type);
        if (node.DisplayName is not null)
        {
            writer.WriteString("displayName", node.DisplayName);
        }

        if (node.Properties.Count > 0)
        {
            writer.WriteStartObject("properties");
            foreach (var property in node.Properties)
            {
                writer.WritePropertyName(property.Name);
                WriteProperty(writer, property.Value);
            }

            writer.WriteEndObject();
        }

        if (node.Children.Count > 0)
        {
            writer.WriteStartArray("children");
            for (var i = 0; i < node.Children.Count; i++)
            {
                WriteNode(writer, node.Children[i], string.Create(CultureInfo.InvariantCulture, $"{jsonPath}.children[{i}]"), path.Child(i), paths);
            }

            writer.WriteEndArray();
        }

        if (node.Slots.Count > 0)
        {
            writer.WriteStartObject("slots");
            foreach (var slot in node.Slots)
            {
                writer.WritePropertyName(slot.Name);
                WriteNode(writer, slot.Node, $"{jsonPath}.slots.{slot.Name}", path.Slot(slot.Name), paths);
            }

            writer.WriteEndObject();
        }

        WriteExtra(writer, node.Extra);
        writer.WriteEndObject();
    }

    private static void WriteProperty(Utf8JsonWriter writer, PropertyDraft value)
    {
        switch (value)
        {
            case ScalarValue scalar:
                WriteScalar(writer, scalar);
                break;
            case MapValue map:
                writer.WriteStartObject();
                foreach (var entry in map.Entries)
                {
                    writer.WritePropertyName(entry.Key);
                    WriteScalar(writer, entry.Value);
                }

                writer.WriteEndObject();
                break;
            default:
                throw new InvalidOperationException($"Unknown property value {value.GetType().Name}.");
        }
    }

    private static void WriteScalar(Utf8JsonWriter writer, ScalarValue scalar)
    {
        if (scalar.IsJsonLiteral)
        {
            writer.WriteRawValue(scalar.Text);
        }
        else
        {
            writer.WriteStringValue(scalar.Text);
        }
    }

    private static void WriteDefault(Utf8JsonWriter writer, string? defaultJson)
    {
        if (defaultJson is not null)
        {
            writer.WritePropertyName("default");
            writer.WriteRawValue(defaultJson);
        }
    }

    private static void WriteExtra(Utf8JsonWriter writer, ImmutableList<JsonField> extra)
    {
        foreach (var field in extra)
        {
            writer.WritePropertyName(field.Name);
            writer.WriteRawValue(field.RawJson);
        }
    }

    private static PropertyDraft ReadProperty(JsonElement element, string path) => element.ValueKind switch
    {
        JsonValueKind.Object => new MapValue([.. element.EnumerateObject().Select(e => new MapEntry(e.Name, ReadScalar(e.Value, $"{path}.{e.Name}")))]),
        _ => ReadScalar(element, path),
    };

    private static ScalarValue ReadScalar(JsonElement element, string path) => element.ValueKind switch
    {
        JsonValueKind.String => new ScalarValue(element.GetString()!),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => new ScalarValue(element.GetRawText(), IsJsonLiteral: true),
        _ => throw new DraftReadException($"{path}: expected a string, number, true, false or null."),
    };

    private static ArgumentDraft ReadArgument(JsonElement element, string path)
    {
        Expect(element, path, JsonValueKind.Object);
        var required = element.TryGetProperty("required", out var r)
            ? r.ValueKind is JsonValueKind.True or JsonValueKind.False ? r.GetBoolean() : throw new DraftReadException($"{path}.required: expected true or false.")
            : false;
        return new ArgumentDraft(
            String(element, "name", path) ?? string.Empty,
            String(element, "direction", path) ?? string.Empty,
            String(element, "type", path) ?? string.Empty,
            required,
            element.TryGetProperty("default", out var d) ? d.GetRawText() : null);
    }

    private static VariableDraft ReadVariable(JsonElement element, string path)
    {
        Expect(element, path, JsonValueKind.Object);
        return new VariableDraft(
            String(element, "name", path) ?? string.Empty,
            String(element, "type", path) ?? string.Empty,
            element.TryGetProperty("default", out var d) ? d.GetRawText() : null);
    }

    private static ImmutableList<T> ReadList<T>(JsonElement parent, string field, string path, Func<JsonElement, string, T> read)
    {
        if (!parent.TryGetProperty(field, out var array))
        {
            return [];
        }

        var list = ImmutableList.CreateBuilder<T>();
        var index = 0;
        foreach (var item in Expect(array, $"{path}.{field}", JsonValueKind.Array).EnumerateArray())
        {
            list.Add(read(item, string.Create(CultureInfo.InvariantCulture, $"{path}.{field}[{index++}]")));
        }

        return list.ToImmutable();
    }

    private static ImmutableList<JsonField> Extra(JsonElement element, HashSet<string> known) =>
        [.. element.EnumerateObject().Where(p => !known.Contains(p.Name)).Select(p => new JsonField(p.Name, p.Value.GetRawText()))];

    private static string? String(JsonElement parent, string field, string path)
    {
        if (!parent.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new DraftReadException($"{path}.{field}: expected a string.");
    }

    private static JsonElement Expect(JsonElement element, string path, JsonValueKind kind) =>
        element.ValueKind == kind ? element : throw new DraftReadException($"{path}: expected {(kind == JsonValueKind.Object ? "an object" : "an array")}.");
}

/// <summary>A workflow file cannot be opened in the designer (invalid JSON or an unrepresentable shape).</summary>
public sealed class DraftReadException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DraftReadException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong, with its JSON path.</param>
    public DraftReadException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="innerException">Cause.</param>
    public DraftReadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
