using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;
using static MyRPA.Activities.BuiltIn.BuiltInProperties;

namespace MyRPA.Activities.BuiltIn;

/// <summary><c>Core.Json.Parse</c>: JSON text to a workflow value.</summary>
public sealed class JsonParseActivity : IActivity
{
    /// <summary>Deepest nesting accepted.</summary>
    public const int MaxDepth = 64;

    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Json.Parse"),
        "Parse JSON",
        CoreActivities.DataCategory,
        "Reads JSON text (for example an API response or a file's content) into a workflow value: objects become Dictionaries, arrays Lists. Fails with InvalidJson for text that is not JSON.",
        [
            Input("text", ActivityValueType.String, "The JSON text.", required: true),
            Result(ActivityValueType.Any, "Receives the value."),
        ]);

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        SetResult(context, Parse(Text(context, "text"), $"'text' of '{context.Node.Id}'"));
        return ActivityResult.CompletedTask;
    }

    /// <summary>Parses JSON text into a canonical value (depth-limited); failures say where, not what the text was.</summary>
    internal static object? Parse(string text, string what)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = MaxDepth });
            return WorkflowValues.TryFromJsonUntyped(document.RootElement, out var value, out var error)
                ? value
                : throw new ActivityFailedException("InvalidJson", $"{what} holds JSON that cannot become a workflow value: {error}");
        }
        catch (JsonException ex)
        {
            throw new ActivityFailedException("InvalidJson", $"{what} is not valid JSON (line {ex.LineNumber + 1}, position {ex.BytePositionInLine + 1}).", ex);
        }
    }
}

/// <summary><c>Core.Json.Serialize</c>: a workflow value to JSON text.</summary>
public sealed class JsonSerializeActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Json.Serialize"),
        "Serialize JSON",
        CoreActivities.DataCategory,
        "Writes a workflow value as JSON text, for example to send it to an API or save it to a file.",
        [
            Input("value", ActivityValueType.Any, "The value.", required: true),
            Input("indented", ActivityValueType.Boolean, "Write one value per line, indented.", defaultJson: "false"),
            Result(ActivityValueType.String, "Receives the JSON text."),
        ]);

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        SetResult(context, Write(context.Evaluate("value"), Flag(context, "indented", false)));
        return ActivityResult.CompletedTask;
    }

    internal static string Write(object? value, bool indented)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            WorkflowValues.WriteJson(writer, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
