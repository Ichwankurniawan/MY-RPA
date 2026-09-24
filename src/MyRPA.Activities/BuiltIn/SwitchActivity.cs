using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;

namespace MyRPA.Activities.BuiltIn;

/// <summary>
/// <c>Core.Switch</c>: evaluates <c>expression</c>, formats it as text, and runs slot <c>case:&lt;text&gt;</c>;
/// otherwise slot <c>default</c> (optional). Matching is exact and case-sensitive.
/// </summary>
public sealed class SwitchActivity : IActivity
{
    /// <summary>Prefix of case slots.</summary>
    public const string CasePrefix = "case:";

    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Switch"),
        "Switch",
        CoreActivities.ControlFlowCategory,
        "Runs the case whose name equals the expression value (as text), or the default.",
        [new("expression", ActivityPropertyKind.Expression, isRequired: true, "Value to match against case names.")],
        slots: [new(CasePrefix, isPrefix: true), new("default")]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var key = CasePrefix + context.EvaluateText("expression");
        if (context.Node.Slots.TryGetValue(key, out var node) || context.Node.Slots.TryGetValue("default", out node))
        {
            await context.ExecuteAsync(node).ConfigureAwait(false);
        }

        return ActivityResult.Completed;
    }
}
