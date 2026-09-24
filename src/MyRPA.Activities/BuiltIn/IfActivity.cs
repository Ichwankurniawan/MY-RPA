using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;

namespace MyRPA.Activities.BuiltIn;

/// <summary><c>Core.If</c>: runs slot <c>then</c> when <c>condition</c> is true, otherwise slot <c>else</c> (optional).</summary>
public sealed class IfActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("If"),
        "If",
        CoreActivities.ControlFlowCategory,
        "Chooses a branch based on a Boolean condition.",
        [new("condition", ActivityPropertyKind.Expression, isRequired: true, "Boolean condition.")],
        slots: [new("then", isRequired: true), new("else")]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var branch = context.EvaluateBoolean("condition") ? "then" : "else";
        if (context.Node.Slots.TryGetValue(branch, out var node))
        {
            await context.ExecuteAsync(node).ConfigureAwait(false);
        }

        return ActivityResult.Completed;
    }
}
