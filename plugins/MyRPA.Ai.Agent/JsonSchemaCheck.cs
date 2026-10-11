using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyRPA.Ai.Agent;

/// <summary>
/// Checks a JSON value against the JSON-schema keywords agents rely on (ADR-0048): type (one or a list), enum, const,
/// properties, required, additionalProperties, items, minItems, maxItems, minLength, maxLength, pattern, minimum,
/// maximum, anyOf, oneOf, allOf. Other keywords are not checked (documented). It never trusts a model's claim of
/// validity; it only reports problems, with their paths.
/// </summary>
internal static class JsonSchemaCheck
{
    private const int MaxProblems = 20;
    private const int MaxDepth = 64;

    public static List<string> Validate(JsonElement schema, JsonElement value)
    {
        var problems = new List<string>();
        Check(schema, value, "$", problems, 0);
        return problems;
    }

    private static void Check(JsonElement schema, JsonElement value, string path, List<string> problems, int depth)
    {
        if (problems.Count >= MaxProblems)
        {
            return;
        }

        if (depth > MaxDepth)
        {
            problems.Add($"{path}: nested too deeply");
            return;
        }

        if (schema.ValueKind == JsonValueKind.False)
        {
            problems.Add($"{path}: no value is allowed here");
            return;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("type", out var type) && !TypeMatches(type, value))
        {
            problems.Add($"{path}: expected {(type.ValueKind == JsonValueKind.Array ? string.Join(" or ", type.EnumerateArray().Select(t => t.GetString())) : type.GetString())}, found {Kind(value)}");
            return;
        }

        if (schema.TryGetProperty("enum", out var options) && options.ValueKind == JsonValueKind.Array && !options.EnumerateArray().Any(o => JsonElement.DeepEquals(o, value)))
        {
            problems.Add($"{path}: must be one of {options.GetRawText()}");
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value))
        {
            problems.Add($"{path}: must be {constant.GetRawText()}");
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                CheckObject(schema, value, path, problems, depth);
                break;
            case JsonValueKind.Array:
                var count = value.GetArrayLength();
                if (Number(schema, "minItems") is { } minItems && count < minItems)
                {
                    problems.Add($"{path}: needs at least {minItems} items");
                }

                if (Number(schema, "maxItems") is { } maxItems && count > maxItems)
                {
                    problems.Add($"{path}: allows at most {maxItems} items");
                }

                if (schema.TryGetProperty("items", out var items))
                {
                    var i = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        Check(items, item, $"{path}[{i++}]", problems, depth + 1);
                    }
                }

                break;
            case JsonValueKind.String:
                var text = value.GetString()!;
                if (Number(schema, "minLength") is { } minLength && text.Length < minLength)
                {
                    problems.Add($"{path}: needs at least {minLength} characters");
                }

                if (Number(schema, "maxLength") is { } maxLength && text.Length > maxLength)
                {
                    problems.Add($"{path}: allows at most {maxLength} characters");
                }

                if (schema.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.String && !Matches(pattern.GetString()!, text))
                {
                    problems.Add($"{path}: does not match the pattern {pattern.GetString()}");
                }

                break;
            case JsonValueKind.Number:
                var number = value.GetDecimal();
                if (Number(schema, "minimum") is { } minimum && number < minimum)
                {
                    problems.Add($"{path}: must be at least {minimum.ToString(CultureInfo.InvariantCulture)}");
                }

                if (Number(schema, "maximum") is { } maximum && number > maximum)
                {
                    problems.Add($"{path}: must be at most {maximum.ToString(CultureInfo.InvariantCulture)}");
                }

                break;
        }

        CheckCombinations(schema, value, path, problems, depth);
    }

    private static void CheckObject(JsonElement schema, JsonElement value, string path, List<string> problems, int depth)
    {
        var properties = schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray().Select(r => r.GetString()).Where(n => n is not null && !value.TryGetProperty(n, out _)))
            {
                problems.Add($"{path}.{name}: is required");
            }
        }

        var additional = schema.TryGetProperty("additionalProperties", out var a) ? a : default;
        foreach (var property in value.EnumerateObject())
        {
            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                Check(propertySchema, property.Value, $"{path}.{property.Name}", problems, depth + 1);
            }
            else if (additional.ValueKind == JsonValueKind.False)
            {
                problems.Add($"{path}.{property.Name}: is not allowed");
            }
            else if (additional.ValueKind == JsonValueKind.Object)
            {
                Check(additional, property.Value, $"{path}.{property.Name}", problems, depth + 1);
            }
        }
    }

    private static void CheckCombinations(JsonElement schema, JsonElement value, string path, List<string> problems, int depth)
    {
        int Passing(JsonElement list) => list.EnumerateArray().Count(s => { var inner = new List<string>(); Check(s, value, path, inner, depth + 1); return inner.Count == 0; });
        if (schema.TryGetProperty("anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array && Passing(anyOf) == 0)
        {
            problems.Add($"{path}: matches none of the allowed shapes (anyOf)");
        }

        if (schema.TryGetProperty("oneOf", out var oneOf) && oneOf.ValueKind == JsonValueKind.Array && Passing(oneOf) != 1)
        {
            problems.Add($"{path}: must match exactly one allowed shape (oneOf)");
        }

        if (schema.TryGetProperty("allOf", out var allOf) && allOf.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in allOf.EnumerateArray())
            {
                Check(part, value, path, problems, depth + 1);
            }
        }
    }

    private static bool TypeMatches(JsonElement type, JsonElement value) => type.ValueKind switch
    {
        JsonValueKind.String => Is(type.GetString()!, value),
        JsonValueKind.Array => type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && Is(t.GetString()!, value)),
        _ => true,
    };

    private static bool Is(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var d) && d == decimal.Truncate(d),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static string Kind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "null",
    };

    private static decimal? Number(JsonElement schema, string name) =>
        schema.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

    private static bool Matches(string pattern, string text)
    {
        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
