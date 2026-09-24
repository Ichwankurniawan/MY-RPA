using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;

namespace MyRPA.Activities.BuiltIn;

/// <summary><c>Core.Throw</c>: fails with the given message (catchable by <c>Core.TryCatch</c>).</summary>
public sealed class ThrowActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Throw"),
        "Throw",
        CoreActivities.ControlFlowCategory,
        "Raises a workflow error.",
        [new("message", ActivityPropertyKind.Expression, isRequired: true, "Error message.")]);

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        throw new WorkflowThrowException(context.EvaluateText("message"));
    }
}
