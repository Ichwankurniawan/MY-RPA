using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MyRPA.Browser.Contracts;
using MyRPA.Sdk.Automation;

namespace MyRPA.Browser.Playwright.Recording;

/// <summary>
/// Recorded steps as workflow nodes of this plugin's activities (ADR-0039, PRD §6.4): Browser.Open at the start URL, one
/// activity per step (Navigate, Click, TypeText, SelectOption, UploadFile, DownloadFile), then Browser.Close. Values are
/// written as expression literals; a password is the In argument <c>password</c>.
/// </summary>
internal static class RecordedActivities
{
    public const string PasswordArgument = "password";

    public static string Generate(Uri startUrl, IReadOnlyList<RecordedStep> steps)
    {
        ArgumentNullException.ThrowIfNull(startUrl);
        ArgumentNullException.ThrowIfNull(steps);
        RequireWebUrl(startUrl.OriginalString, "The start URL");
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        string Id(string name)
        {
            ids[name] = ids.GetValueOrDefault(name) + 1;
            return string.Create(CultureInfo.InvariantCulture, $"{name}-{ids[name]}");
        }

        using var buffer = new MemoryStream();
        // Readable literals, as WorkflowJsonWriter writes them (the Studio parses the JSON; nothing is embedded in HTML).
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("nodes");
            Node(writer, Id("open"), "Browser.Open", $"Open {startUrl.Host}", w => w.WriteString("url", Literal(startUrl.AbsoluteUri)));
            foreach (var step in steps)
            {
                WriteStep(writer, step, Id);
            }

            Node(writer, Id("close"), "Browser.Close", null, null);
            writer.WriteEndArray();
            writer.WriteStartArray("arguments");
            if (steps.Any(s => s.Kind == RecordedStepKind.Type && s.Secret))
            {
                writer.WriteStartObject();
                writer.WriteString("name", PasswordArgument);
                writer.WriteString("direction", "In");
                writer.WriteString("type", "String");
                writer.WriteBoolean("required", true);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>An expression string literal (ADR-0009): single-quoted, with backslash, quote, newline and tab escaped.</summary>
    internal static string Literal(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append('\'');
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '\\' => @"\\",
                '\'' => @"\'",
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                _ => c.ToString(),
            });
        }

        return builder.Append('\'').ToString();
    }

    private static void WriteStep(Utf8JsonWriter writer, RecordedStep step, Func<string, string> id)
    {
        if (step.Kind == RecordedStepKind.Navigate)
        {
            var url = step.Url ?? throw new ArgumentException($"Step {step.Sequence}: a navigation needs a URL.", nameof(step));
            RequireWebUrl(url, $"Step {step.Sequence}");
            Node(writer, id("navigate"), "Browser.Navigate", $"Go to {url}", w => w.WriteString("url", Literal(url)));
            return;
        }

        var selector = step.Selector ?? throw new ArgumentException($"Step {step.Sequence}: a {step.Kind} step needs a selector.", nameof(step));
        try
        {
            BrowserSelectors.Parse(selector);
        }
        catch (AutomationException ex)
        {
            throw new ArgumentException($"Step {step.Sequence}: {ex.Message}", nameof(step), ex);
        }

        var element = step.Element is { Length: > 0 } e ? " " + e : string.Empty;
        switch (step.Kind)
        {
            case RecordedStepKind.Click:
                Node(writer, id("click"), "Browser.Click", "Click" + element, w => w.WriteString("selector", selector));
                break;
            case RecordedStepKind.Type:
                Node(writer, id("type"), "Browser.TypeText", "Type into" + element, w =>
                {
                    w.WriteString("selector", selector);
                    w.WriteString("text", step.Secret ? PasswordArgument : Literal(step.Text ?? string.Empty));
                });
                break;
            case RecordedStepKind.Select:
                Node(writer, id("select"), "Browser.SelectOption", "Select in" + element, w =>
                {
                    w.WriteString("selector", selector);
                    w.WriteString("value", Values(step));
                });
                break;
            case RecordedStepKind.Upload:
                Node(writer, id("upload"), "Browser.UploadFile", "Upload to" + element, w =>
                {
                    w.WriteString("selector", selector);
                    w.WriteString("files", Values(step));
                });
                break;
            case RecordedStepKind.Download:
                Node(writer, id("download"), "Browser.DownloadFile", "Download from" + element, w =>
                {
                    w.WriteString("selector", selector);
                    w.WriteString("path", Literal(step.FileName is { Length: > 0 } name ? name : "download"));
                });
                break;
            default:
                throw new ArgumentException($"Step {step.Sequence}: '{step.Kind}' is not a recorded step kind.", nameof(step));
        }
    }

    private static void Node(Utf8JsonWriter writer, string id, string type, string? displayName, Action<Utf8JsonWriter>? properties)
    {
        writer.WriteStartObject();
        writer.WriteString("id", id);
        writer.WriteString("type", type);
        if (displayName is not null)
        {
            writer.WriteString("displayName", displayName.Length > 120 ? displayName[..120] : displayName);
        }

        if (properties is not null)
        {
            writer.WriteStartObject("properties");
            properties(writer);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    /// <summary>One value as a String literal, several as a List literal.</summary>
    private static string Values(RecordedStep step) =>
        step.Values.Count == 1 ? Literal(step.Values[0]) : "[" + string.Join(", ", step.Values.Select(Literal)) + "]";

    private static void RequireWebUrl(string url, string what)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"{what}: '{url}' is not an absolute http or https URL.", nameof(url));
        }
    }
}
