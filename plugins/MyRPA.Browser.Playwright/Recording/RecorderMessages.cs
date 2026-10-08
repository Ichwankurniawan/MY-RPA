using System.Text.Json;
using MyRPA.Browser.Contracts;
using MyRPA.Sdk.Automation;

namespace MyRPA.Browser.Playwright.Recording;

/// <summary>
/// One message from <c>recorder.js</c>, validated (ADR-0039 threat model: the recorded site can call the binding too, so
/// every field is checked and a malformed message is dropped; selectors must parse).
/// </summary>
internal sealed record RecorderMessage(RecordedStepKind Kind, int Element, IReadOnlyList<(string Selector, bool Unique)> Candidates, string? Label, string? Text, bool Secret, IReadOnlyList<string> Values)
{
    public const int MaxLength = 32 * 1024;

    /// <summary>The message, or null when it is not a well-formed recorder message.</summary>
    public static RecorderMessage? Parse(string? json)
    {
        if (json is null || json.Length > MaxLength)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            RecordedStepKind? kind = kindElement.GetString() switch
            {
                "click" => RecordedStepKind.Click,
                "type" => RecordedStepKind.Type,
                "select" => RecordedStepKind.Select,
                "upload" => RecordedStepKind.Upload,
                _ => null,
            };
            if (kind is null || !root.TryGetProperty("element", out var elementId) || !elementId.TryGetInt32(out var element) || element <= 0)
            {
                return null;
            }

            var candidates = new List<(string, bool)>();
            if (root.TryGetProperty("candidates", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray().Take(12))
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("selector", out var selector) && selector.ValueKind == JsonValueKind.String
                        && selector.GetString() is { Length: > 0 and <= 512 } text
                        && Parses(text))
                    {
                        candidates.Add((text, item.TryGetProperty("unique", out var unique) && unique.ValueKind == JsonValueKind.True));
                    }
                }
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            var secret = root.TryGetProperty("secret", out var secretElement) && secretElement.ValueKind == JsonValueKind.True;
            var values = new List<string>();
            if (root.TryGetProperty("values", out var valueList) && valueList.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in valueList.EnumerateArray().Take(20))
                {
                    if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 255 } text
                        && (kind != RecordedStepKind.Upload || (text.Length > 0 && text.IndexOfAny(['/', '\\']) < 0)))
                    {
                        values.Add(text);
                    }
                }
            }

            return new RecorderMessage(kind.Value, element, candidates, String(root, "label", 200), secret ? null : String(root, "text", 4096), secret, values);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement root, string name, int max) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            ? (text.Length > max ? text[..max] : text)
            : null;

    private static bool Parses(string selector)
    {
        try
        {
            BrowserSelectors.Parse(selector);
            return true;
        }
        catch (AutomationException)
        {
            return false;
        }
    }
}
