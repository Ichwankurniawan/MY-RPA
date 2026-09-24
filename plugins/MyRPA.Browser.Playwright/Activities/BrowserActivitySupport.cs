using MyRPA.Core.Activities;
using MyRPA.Sdk.Automation;
using MyRPA.Workflow.Execution;

namespace MyRPA.Browser.Playwright.Activities;

/// <summary>Property definitions and helpers shared by the browser activities.</summary>
internal static class BrowserActivitySupport
{
    /// <summary>Extra time given to Playwright past the run's deadline so the run's own timeout fires first (TimedOut, not a node failure).</summary>
    private static readonly TimeSpan _deadlineGrace = TimeSpan.FromMilliseconds(250);

    public const string Category = "Browser";

    public static ActivityPropertyDefinition SessionProperty { get; } =
        new("session", ActivityPropertyKind.Expression, isRequired: false, "Session id from Browser.Open; defaults to the only open session.");

    public static ActivityPropertyDefinition TimeoutProperty { get; } =
        new("timeoutMilliseconds", ActivityPropertyKind.Expression, isRequired: false, "Timeout (Int, > 0); defaults to the plugin's defaultTimeoutMilliseconds.");

    public static ActivityPropertyDefinition SelectorProperty { get; } =
        new("selector", ActivityPropertyKind.Text, isRequired: true, "Browser selector: css=…, xpath=…, text=…, role=name|accessible name; steps joined by ' >> '.");

    public static ActivityTypeName Name(string name) => new("Browser." + name);

    /// <summary>The operation limits: the requested timeout, shortened to the run's deadline (plus a grace), and the token.</summary>
    public static OperationLimits Limits(IActivityContext context, BrowserPluginOptions options)
    {
        var requested = options.DefaultTimeout;
        if (context.HasProperty("timeoutMilliseconds"))
        {
            var ms = context.EvaluateInt("timeoutMilliseconds");
            requested = ms > 0
                ? TimeSpan.FromMilliseconds(ms)
                : throw new ActivityFailedException(BrowserErrorTypes.InvalidArgument, "timeoutMilliseconds must be positive.");
        }

        if (context.Deadline is { } deadline)
        {
            var remaining = deadline - context.TimeProvider.GetUtcNow();
            if (remaining + _deadlineGrace < requested)
            {
                requested = remaining > TimeSpan.Zero ? remaining + _deadlineGrace : _deadlineGrace;
            }
        }

        return new OperationLimits(requested, context.CancellationToken);
    }

    public static IBrowserSession Session(IActivityContext context, BrowserSessions sessions) =>
        sessions.Get(context.HasProperty("session") ? context.EvaluateText("session") : null);

    public static Selector Selector(IActivityContext context) => BrowserSelectors.Parse(context.GetText("selector"));

    /// <summary>Evaluates a property that is a String or a List of Strings.</summary>
    public static IReadOnlyList<string> Strings(IActivityContext context, string property)
    {
        return context.Evaluate(property) switch
        {
            string single => [single],
            IReadOnlyList<object?> list when list.All(v => v is string) => [.. list.Cast<string>()],
            var other => throw new ActivityFailedException(
                BrowserErrorTypes.InvalidArgument, $"'{property}' must be a String or a List of Strings, not {Describe(other)}."),
        };
    }

    private static string Describe(object? value) => value is null ? "null" : value.GetType().Name;
}
