using Microsoft.Extensions.Logging;
using MyRPA.Core.Activities;
using MyRPA.Sdk.Automation;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;

namespace MyRPA.Samples.DemoPlugin;

/// <summary>
/// <c>Demo.Echo</c>: evaluates <c>text</c>, prefixes it with the plugin's <c>echoPrefix</c> setting, logs it and
/// optionally assigns it to <c>to</c>. Deterministic; shows settings and logging reaching an activity.
/// </summary>
/// <param name="options">Plugin settings (plugin-owned instance).</param>
/// <param name="logger">Logger; entries are correlated with the execution and node automatically.</param>
public sealed partial class EchoActivity(DemoOptions options, ILogger<EchoActivity> logger) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Demo.Echo"),
        "Echo",
        "Demo",
        "Returns its text, prefixed by the plugin's echoPrefix setting.",
        [
            new("text", ActivityPropertyKind.Expression, isRequired: true, "Text to echo."),
            new("to", ActivityPropertyKind.AssignmentTarget, isRequired: false, "Receives the echoed text."),
        ]);

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var echoed = options.EchoPrefix + WorkflowValues.ToDisplayString(context.Evaluate("text"));
        LogEcho(logger, echoed);
        if (context.HasProperty("to"))
        {
            context.SetValue(context.GetName("to"), echoed);
        }

        return ActivityResult.CompletedTask;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Echo: {Text}")]
    private static partial void LogEcho(ILogger logger, string text);
}

/// <summary>
/// <c>Demo.GetField</c>: the provider pattern end to end — open a document, build a <see cref="Selector"/>, resolve it
/// through <see cref="ISelectorResolver"/>, and read the <see cref="IAutomationElement"/>. A missing field fails the
/// node with error type <c>ElementNotFound</c>, which <c>Core.TryCatch</c> can inspect.
/// </summary>
/// <param name="provider">The provider, injected through its technology interface.</param>
public sealed class GetFieldActivity(IDemoTextProvider provider) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Demo.GetField"),
        "Get Field",
        "Demo",
        "Reads one field of a document through the Demo.Text provider.",
        [
            new("document", ActivityPropertyKind.Expression, isRequired: true, "The document (Dictionary)."),
            new("field", ActivityPropertyKind.Text, isRequired: true, "Field name (selector value)."),
            new("to", ActivityPropertyKind.AssignmentTarget, isRequired: true, "Receives the field text."),
        ]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Evaluate("document") is not IReadOnlyDictionary<string, object?> fields)
        {
            throw new ActivityFailedException("InvalidArgument", "'document' must evaluate to a Dictionary.");
        }

        var root = provider.OpenDocument(fields);
        var selector = new Selector(DemoTextProvider.Id, [new SelectorStep(DemoTextProvider.FieldStrategy, context.GetText("field"))]);
        var match = await provider.ResolveAsync(selector, root, context.CancellationToken).ConfigureAwait(false);
        var element = match.RequireSingle();
        context.SetValue(context.GetName("to"), await element.GetTextAsync(context.CancellationToken).ConfigureAwait(false));
        return ActivityResult.Completed;
    }
}
