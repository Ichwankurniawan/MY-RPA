using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;

namespace MyRPA.Activities.BuiltIn;

/// <summary><c>Core.Sequence</c>: runs its children in order.</summary>
public sealed class SequenceActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Sequence"),
        "Sequence",
        CoreActivities.ControlFlowCategory,
        "Runs its children in order; stops at the first failure.",
        allowsChildren: true);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var child in context.Node.Children)
        {
            await context.ExecuteAsync(child).ConfigureAwait(false);
        }

        return ActivityResult.Completed;
    }
}
