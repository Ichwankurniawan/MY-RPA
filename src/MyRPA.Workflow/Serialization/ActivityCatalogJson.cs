using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MyRPA.Core.Activities;

namespace MyRPA.Workflow.Serialization;

/// <summary>
/// Serializes activity metadata (<see cref="ActivityDescriptor"/>s) to JSON and back (ADR-0020). A snapshot lets tools
/// that must not load plugin code — a web designer, an orchestrator validating an uploaded workflow — validate workflows
/// with the same <c>WorkflowLoader</c>, and describe properties, slots and allowed values.
/// </summary>
/// <remarks>
/// Format (<see cref="FormatVersion"/>):
/// <c>{ "catalogVersion": "1.0", "activities": [ { "type", "displayName", "category", "description", "allowsChildren",
/// "properties": [ { "name", "kind", "required", "description", "allowedValues", "scopeSlots" } ],
/// "slots": [ { "name", "required", "prefix", "description" } ] } ] }</c>.
/// </remarks>
public static class ActivityCatalogJson
{
    /// <summary>The snapshot format version written and read by this build.</summary>
    public const string FormatVersion = "1.0";

    private static readonly JsonWriterOptions _writerOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Writes the descriptors, ordered by type name.</summary>
    /// <param name="descriptors">Activity metadata.</param>
    public static string Write(IEnumerable<ActivityDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, _writerOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("catalogVersion", FormatVersion);
            writer.WriteStartArray("activities");
            foreach (var descriptor in descriptors.OrderBy(d => d.TypeName.Value, StringComparer.Ordinal))
            {
                WriteDescriptor(writer, descriptor);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Reads a snapshot.</summary>
    /// <param name="json">Snapshot JSON.</param>
    /// <exception cref="FormatException">The JSON is not a valid snapshot; the message names the problem and its path.</exception>
    public static ActivityCatalogSnapshot Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = Object(document.RootElement, "$");
            var version = String(root, "catalogVersion", "$");
            if (!string.Equals(version, FormatVersion, StringComparison.Ordinal))
            {
                throw new FormatException($"$.catalogVersion: '{version}' is not supported; this build reads {FormatVersion}.");
            }

            var descriptors = new List<ActivityDescriptor>();
            var index = 0;
            foreach (var element in Array(root, "activities", "$"))
            {
                descriptors.Add(ReadDescriptor(element, $"$.activities[{index++}]"));
            }

            return new ActivityCatalogSnapshot(descriptors);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"The activity catalog is not valid JSON: {ex.Message}", ex);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"The activity catalog contains invalid metadata: {ex.Message}", ex);
        }
    }

    private static void WriteDescriptor(Utf8JsonWriter writer, ActivityDescriptor descriptor)
    {
        writer.WriteStartObject();
        writer.WriteString("type", descriptor.TypeName.Value);
        writer.WriteString("displayName", descriptor.DisplayName);
        writer.WriteString("category", descriptor.Category);
        if (descriptor.Description is not null)
        {
            writer.WriteString("description", descriptor.Description);
        }

        writer.WriteBoolean("allowsChildren", descriptor.AllowsChildren);
        writer.WriteStartArray("properties");
        foreach (var property in descriptor.Properties)
        {
            writer.WriteStartObject();
            writer.WriteString("name", property.Name);
            writer.WriteString("kind", property.Kind.ToString());
            writer.WriteBoolean("required", property.IsRequired);
            if (property.Description is not null)
            {
                writer.WriteString("description", property.Description);
            }

            WriteStrings(writer, "allowedValues", property.AllowedValues);
            WriteStrings(writer, "scopeSlots", property.ScopeSlots);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("slots");
        foreach (var slot in descriptor.Slots)
        {
            writer.WriteStartObject();
            writer.WriteString("name", slot.Name);
            writer.WriteBoolean("required", slot.IsRequired);
            writer.WriteBoolean("prefix", slot.IsPrefix);
            if (slot.Description is not null)
            {
                writer.WriteString("description", slot.Description);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static ActivityDescriptor ReadDescriptor(JsonElement element, string path)
    {
        Object(element, path);
        var typeText = String(element, "type", path);
        if (!ActivityTypeName.TryCreate(typeText, out var type))
        {
            throw new FormatException($"{path}.type: '{typeText}' is not a valid activity type name.");
        }

        var properties = new List<ActivityPropertyDefinition>();
        var index = 0;
        foreach (var property in Array(element, "properties", path))
        {
            var propertyPath = $"{path}.properties[{index++}]";
            Object(property, propertyPath);
            var kindText = String(property, "kind", propertyPath);
            if (!Enum.TryParse<ActivityPropertyKind>(kindText, ignoreCase: false, out var kind) || !Enum.IsDefined(kind) || int.TryParse(kindText, out _))
            {
                throw new FormatException($"{propertyPath}.kind: '{kindText}' is not a property kind.");
            }

            properties.Add(new ActivityPropertyDefinition(
                String(property, "name", propertyPath),
                kind,
                Boolean(property, "required", propertyPath),
                OptionalString(property, "description", propertyPath),
                Strings(property, "allowedValues", propertyPath),
                Strings(property, "scopeSlots", propertyPath)));
        }

        var slots = new List<ActivitySlotDefinition>();
        index = 0;
        foreach (var slot in Array(element, "slots", path))
        {
            var slotPath = $"{path}.slots[{index++}]";
            Object(slot, slotPath);
            slots.Add(new ActivitySlotDefinition(
                String(slot, "name", slotPath),
                Boolean(slot, "required", slotPath),
                Boolean(slot, "prefix", slotPath),
                OptionalString(slot, "description", slotPath)));
        }

        return new ActivityDescriptor(
            type,
            String(element, "displayName", path),
            String(element, "category", path),
            OptionalString(element, "description", path),
            properties,
            Boolean(element, "allowsChildren", path),
            slots);
    }

    private static JsonElement Object(JsonElement element, string path) =>
        element.ValueKind == JsonValueKind.Object ? element : throw new FormatException($"{path}: expected an object.");

    private static JsonElement.ArrayEnumerator Array(JsonElement parent, string name, string path) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : throw new FormatException($"{path}.{name}: expected an array.");

    private static string String(JsonElement parent, string name, string path) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new FormatException($"{path}.{name}: expected a non-empty string.");

    private static string? OptionalString(JsonElement parent, string name, string path)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException($"{path}.{name}: expected a string.");
    }

    private static bool Boolean(JsonElement parent, string name, string path) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new FormatException($"{path}.{name}: expected true or false.");

    private static List<string> Strings(JsonElement parent, string name, string path)
    {
        var result = new List<string>();
        if (!parent.TryGetProperty(name, out var value))
        {
            return result;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException($"{path}.{name}: expected an array of strings.");
        }

        foreach (var item in value.EnumerateArray())
        {
            result.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new FormatException($"{path}.{name}: expected strings only."));
        }

        return result;
    }
}

/// <summary>An <see cref="IActivityCatalog"/> read from JSON: metadata only, no behavior.</summary>
public sealed class ActivityCatalogSnapshot : IActivityCatalog
{
    private readonly Dictionary<ActivityTypeName, ActivityDescriptor> _byName;

    /// <summary>Creates the snapshot.</summary>
    /// <param name="descriptors">Descriptors; type names must be unique.</param>
    /// <exception cref="ArgumentException">A type name occurs more than once.</exception>
    public ActivityCatalogSnapshot(IEnumerable<ActivityDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        _byName = [];
        foreach (var descriptor in descriptors)
        {
            if (!_byName.TryAdd(descriptor.TypeName, descriptor))
            {
                throw new ArgumentException($"Activity type '{descriptor.TypeName}' occurs more than once.", nameof(descriptors));
            }
        }

        Descriptors = [.. _byName.Values.OrderBy(d => d.TypeName.Value, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public IReadOnlyList<ActivityDescriptor> Descriptors { get; }

    /// <inheritdoc />
    public bool TryGet(ActivityTypeName typeName, [NotNullWhen(true)] out ActivityDescriptor? descriptor)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        return _byName.TryGetValue(typeName, out descriptor);
    }
}
