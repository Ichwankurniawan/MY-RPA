using System.Collections;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MyRPA.Workflow.Values;

/// <summary>
/// The workflow value model (ADR-0010). Runtime values are restricted to a closed set of canonical representations:
/// <see langword="null"/>, <see cref="string"/>, <see cref="long"/>, <see cref="decimal"/>, <see cref="bool"/>,
/// <see cref="DateTimeOffset"/>, <see cref="IReadOnlyList{T}"/> of values, and <see cref="IReadOnlyDictionary{TKey,TValue}"/>
/// with string keys. No arbitrary CLR objects ever enter a workflow, so no reflection is needed to handle values.
/// </summary>
public static class WorkflowValues
{
    /// <summary>Creates a canonical list from canonical items (copied).</summary>
    /// <param name="items">Items; each must already be canonical.</param>
    public static IReadOnlyList<object?> List(IEnumerable<object?> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new ReadOnlyCollection<object?>([.. items]);
    }

    /// <summary>Creates a canonical dictionary (ordinal keys, copied).</summary>
    /// <param name="entries">Entries; values must already be canonical. Duplicate keys throw.</param>
    public static IReadOnlyDictionary<string, object?> Dictionary(IEnumerable<KeyValuePair<string, object?>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!map.TryAdd(entry.Key, entry.Value))
            {
                throw new ArgumentException($"Duplicate key '{entry.Key}'.", nameof(entries));
            }
        }

        return new ReadOnlyDictionary<string, object?>(map);
    }

    /// <summary>Parses a data type name (<c>String</c>, <c>Int</c>, ...). Case-sensitive.</summary>
    /// <param name="text">Type name.</param>
    /// <param name="type">Parsed type.</param>
    public static bool TryParseDataType(string? text, out WorkflowDataType type)
    {
        type = default;
        return text is not null
            && !char.IsAsciiDigit(text.FirstOrDefault())
            && Enum.TryParse(text, ignoreCase: false, out type)
            && Enum.IsDefined(type);
    }

    /// <summary>Returns the workflow type name of a canonical value (<c>Null</c> for null).</summary>
    /// <param name="value">A canonical value.</param>
    public static string TypeNameOf(object? value) => value switch
    {
        null => "Null",
        string => nameof(WorkflowDataType.String),
        long => nameof(WorkflowDataType.Int),
        decimal => nameof(WorkflowDataType.Decimal),
        bool => nameof(WorkflowDataType.Boolean),
        DateTimeOffset => nameof(WorkflowDataType.DateTime),
        IReadOnlyList<object?> => nameof(WorkflowDataType.List),
        IReadOnlyDictionary<string, object?> => nameof(WorkflowDataType.Dictionary),
        _ => value.GetType().Name,
    };

    /// <summary>
    /// Converts a value supplied by host code (CLI, Studio, tests) into its canonical representation.
    /// Only primitive CLR types, lists and string-keyed dictionaries are accepted; anything else is rejected.
    /// </summary>
    /// <param name="value">Host value.</param>
    /// <param name="canonical">Canonical value.</param>
    /// <param name="error">Reason when conversion fails.</param>
    public static bool TryNormalize(object? value, out object? canonical, [NotNullWhen(false)] out string? error)
    {
        error = null;
        switch (value)
        {
            case null:
                canonical = null;
                return true;
            case string or long or decimal or bool or DateTimeOffset:
                canonical = value;
                return true;
            case int i:
                canonical = (long)i;
                return true;
            case short s:
                canonical = (long)s;
                return true;
            case byte b:
                canonical = (long)b;
                return true;
            case double d when double.IsFinite(d):
                canonical = (decimal)d;
                return true;
            case float f when float.IsFinite(f):
                canonical = (decimal)f;
                return true;
            case DateTime dt:
                canonical = dt.Kind == DateTimeKind.Unspecified
                    ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc))
                    : new DateTimeOffset(dt);
                return true;
            case JsonElement element:
                return TryFromJsonUntyped(element, out canonical, out error);
            case IDictionary dictionary:
                return TryNormalizeDictionary(dictionary, out canonical, out error);
            case IEnumerable enumerable:
                var items = new List<object?>();
                foreach (var item in enumerable)
                {
                    if (!TryNormalize(item, out var normalized, out error))
                    {
                        canonical = null;
                        return false;
                    }

                    items.Add(normalized);
                }

                canonical = List(items);
                return true;
            default:
                canonical = null;
                error = $"Values of type '{value.GetType().Name}' cannot be used in a workflow.";
                return false;
        }
    }

    /// <summary>
    /// Converts a canonical value for storage in a variable of <paramref name="target"/> type.
    /// <see langword="null"/> is accepted for every type; Int widens to Decimal; otherwise types must match.
    /// </summary>
    /// <param name="value">Canonical value.</param>
    /// <param name="target">Declared type.</param>
    /// <param name="result">Converted value.</param>
    /// <param name="error">Reason when conversion fails.</param>
    public static bool TryConvert(object? value, WorkflowDataType target, out object? result, [NotNullWhen(false)] out string? error)
    {
        error = null;
        result = value;
        if (value is null || target == WorkflowDataType.Object)
        {
            return true;
        }

        var ok = target switch
        {
            WorkflowDataType.String => value is string,
            WorkflowDataType.Int => value is long,
            WorkflowDataType.Decimal => value is decimal || value is long,
            WorkflowDataType.Boolean => value is bool,
            WorkflowDataType.DateTime => value is DateTimeOffset,
            WorkflowDataType.List => value is IReadOnlyList<object?>,
            WorkflowDataType.Dictionary => value is IReadOnlyDictionary<string, object?>,
            _ => false,
        };

        if (!ok)
        {
            result = null;
            error = $"Cannot store a {TypeNameOf(value)} value in a {target}.";
            return false;
        }

        if (target == WorkflowDataType.Decimal && value is long l)
        {
            result = (decimal)l;
        }

        return true;
    }

    /// <summary>Reads a JSON value as the given declared type (used for defaults in workflow files).</summary>
    /// <param name="element">JSON value.</param>
    /// <param name="type">Declared type.</param>
    /// <param name="value">Canonical value.</param>
    /// <param name="error">Reason when the JSON value does not fit the type.</param>
    public static bool TryFromJson(JsonElement element, WorkflowDataType type, out object? value, [NotNullWhen(false)] out string? error)
    {
        value = null;
        error = null;
        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        switch (type)
        {
            case WorkflowDataType.String when element.ValueKind == JsonValueKind.String:
                value = element.GetString();
                return true;
            case WorkflowDataType.Int when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var l):
                value = l;
                return true;
            case WorkflowDataType.Decimal when element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var d):
                value = d;
                return true;
            case WorkflowDataType.Boolean when element.ValueKind is JsonValueKind.True or JsonValueKind.False:
                value = element.GetBoolean();
                return true;
            case WorkflowDataType.DateTime when element.ValueKind == JsonValueKind.String:
                if (TryParseDateTime(element.GetString()!, out var dto))
                {
                    value = dto;
                    return true;
                }

                error = "Expected an ISO 8601 date/time string.";
                return false;
            case WorkflowDataType.List when element.ValueKind == JsonValueKind.Array:
            case WorkflowDataType.Dictionary when element.ValueKind == JsonValueKind.Object:
            case WorkflowDataType.Object:
                return TryFromJsonUntyped(element, out value, out error);
            default:
                error = $"A JSON {element.ValueKind} is not a valid {type} value.";
                return false;
        }
    }

    /// <summary>Reads any JSON value into its canonical representation (integers become Int, other numbers Decimal).</summary>
    /// <param name="element">JSON value.</param>
    /// <param name="value">Canonical value.</param>
    /// <param name="error">Reason when a number is out of range or an object has duplicate keys.</param>
    public static bool TryFromJsonUntyped(JsonElement element, out object? value, [NotNullWhen(false)] out string? error)
    {
        error = null;
        value = null;
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.String:
                value = element.GetString();
                return true;
            case JsonValueKind.True or JsonValueKind.False:
                value = element.GetBoolean();
                return true;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var l))
                {
                    value = l;
                    return true;
                }

                if (element.TryGetDecimal(out var d))
                {
                    value = d;
                    return true;
                }

                error = $"Number {element.GetRawText()} is out of range.";
                return false;
            case JsonValueKind.Array:
                var items = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    if (!TryFromJsonUntyped(item, out var itemValue, out error))
                    {
                        return false;
                    }

                    items.Add(itemValue);
                }

                value = List(items);
                return true;
            case JsonValueKind.Object:
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!TryFromJsonUntyped(property.Value, out var propertyValue, out error))
                    {
                        return false;
                    }

                    if (!map.TryAdd(property.Name, propertyValue))
                    {
                        error = $"Duplicate key '{property.Name}'.";
                        return false;
                    }
                }

                value = new ReadOnlyDictionary<string, object?>(map);
                return true;
            default:
                error = $"Unsupported JSON value kind {element.ValueKind}.";
                return false;
        }
    }

    /// <summary>
    /// Parses command-line text as the given type: String is taken verbatim; Int/Decimal/Boolean/DateTime use invariant
    /// formats; List/Dictionary/Object are parsed as JSON.
    /// </summary>
    /// <param name="text">Input text.</param>
    /// <param name="type">Declared type.</param>
    /// <param name="value">Canonical value.</param>
    /// <param name="error">Reason when parsing fails.</param>
    public static bool TryParseText(string text, WorkflowDataType type, out object? value, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        value = null;
        error = null;
        switch (type)
        {
            case WorkflowDataType.String:
                value = text;
                return true;
            case WorkflowDataType.Int when long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l):
                value = l;
                return true;
            case WorkflowDataType.Decimal when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d):
                value = d;
                return true;
            case WorkflowDataType.Boolean when bool.TryParse(text, out var b):
                value = b;
                return true;
            case WorkflowDataType.DateTime when TryParseDateTime(text, out var dto):
                value = dto;
                return true;
            case WorkflowDataType.List or WorkflowDataType.Dictionary or WorkflowDataType.Object:
                try
                {
                    using var document = JsonDocument.Parse(text);
                    return TryFromJson(document.RootElement, type, out value, out error);
                }
                catch (JsonException ex)
                {
                    error = $"Invalid JSON: {ex.Message}";
                    return false;
                }

            default:
                error = $"'{text}' is not a valid {type} value.";
                return false;
        }
    }

    /// <summary>Parses an ISO 8601 date/time; values without an offset are treated as UTC.</summary>
    /// <param name="text">Text.</param>
    /// <param name="value">Parsed value.</param>
    public static bool TryParseDateTime(string text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);

    /// <summary>Writes a canonical value as JSON.</summary>
    /// <param name="writer">Destination.</param>
    /// <param name="value">Canonical value.</param>
    public static void WriteJson(Utf8JsonWriter writer, object? value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case decimal d:
                writer.WriteNumberValue(d);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("O", CultureInfo.InvariantCulture));
                break;
            case IReadOnlyDictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var (key, item) in map)
                {
                    writer.WritePropertyName(key);
                    WriteJson(writer, item);
                }

                writer.WriteEndObject();
                break;
            case IReadOnlyList<object?> list:
                writer.WriteStartArray();
                foreach (var item in list)
                {
                    WriteJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                throw new ArgumentException($"'{value.GetType().Name}' is not a workflow value.", nameof(value));
        }
    }

    /// <summary>Formats a canonical value as text (invariant culture). Null becomes an empty string.</summary>
    /// <param name="value">Canonical value.</param>
    public static string ToDisplayString(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        long l => l.ToString(CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        _ => ToJsonString(value),
    };

    /// <summary>Serializes a canonical value to compact JSON text.</summary>
    /// <param name="value">Canonical value.</param>
    public static string ToJsonString(object? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            WriteJson(writer, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Structural equality: numbers compare by value across Int/Decimal; lists and dictionaries deeply.</summary>
    /// <param name="left">Canonical value.</param>
    /// <param name="right">Canonical value.</param>
    public static bool AreEqual(object? left, object? right)
    {
        switch (left, right)
        {
            case (null, null):
                return true;
            case (null, _) or (_, null):
                return false;
            case (long a, long b):
                return a == b;
            case (long or decimal, long or decimal):
                return ToDecimal(left) == ToDecimal(right);
            case (string a, string b):
                return string.Equals(a, b, StringComparison.Ordinal);
            case (bool a, bool b):
                return a == b;
            case (DateTimeOffset a, DateTimeOffset b):
                return a == b;
            case (IReadOnlyDictionary<string, object?> a, IReadOnlyDictionary<string, object?> b):
                return a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && AreEqual(kv.Value, other));
            case (IReadOnlyList<object?> a, IReadOnlyList<object?> b):
                if (a.Count != b.Count)
                {
                    return false;
                }

                for (var i = 0; i < a.Count; i++)
                {
                    if (!AreEqual(a[i], b[i]))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>Orders two values of comparable types (numbers, strings ordinal, date/times).</summary>
    /// <param name="left">Canonical value.</param>
    /// <param name="right">Canonical value.</param>
    /// <param name="result">Negative, zero or positive.</param>
    public static bool TryCompare(object? left, object? right, out int result)
    {
        switch (left, right)
        {
            case (long a, long b):
                result = a.CompareTo(b);
                return true;
            case (long or decimal, long or decimal):
                result = ToDecimal(left).CompareTo(ToDecimal(right));
                return true;
            case (string a, string b):
                result = string.CompareOrdinal(a, b);
                return true;
            case (DateTimeOffset a, DateTimeOffset b):
                result = a.CompareTo(b);
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static decimal ToDecimal(object? value) => value is long l ? l : (decimal)value!;

    private static bool TryNormalizeDictionary(IDictionary dictionary, out object? canonical, out string? error)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is not string key)
            {
                canonical = null;
                error = "Dictionary keys must be strings.";
                return false;
            }

            if (!TryNormalize(entry.Value, out var normalized, out error))
            {
                canonical = null;
                return false;
            }

            map[key] = normalized;
        }

        canonical = new ReadOnlyDictionary<string, object?>(map);
        error = null;
        return true;
    }
}
