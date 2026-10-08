using System.Globalization;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;

namespace MyRPA.Activities.BuiltIn;

/// <summary>
/// <c>Core.Flowchart</c>: a graph container (ADR-0037). Runs its first child (the start step), then follows each step's
/// transitions until none is taken. Exceeding <c>maxSteps</c> fails the node with MYRPA2010.
/// </summary>
public sealed class FlowchartActivity : IActivity
{
    /// <summary>The step limit when <c>maxSteps</c> is not set.</summary>
    public const long DefaultMaxSteps = 10_000;

    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Flowchart"),
        "Flowchart",
        CoreActivities.ControlFlowCategory,
        "Runs steps from the first one, following each step's transitions; ends when no transition is taken.",
        [new("maxSteps", ActivityPropertyKind.Expression, isRequired: false, "Optional safety limit (Int, default 10,000); exceeding it fails the flowchart.")],
        allowsChildren: true,
        childLayout: ActivityChildLayout.Graph);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var limit = context.HasProperty("maxSteps") ? context.EvaluateInt("maxSteps") : DefaultMaxSteps;
        if (limit < 0)
        {
            throw new InvalidOperationException("maxSteps must not be negative.");
        }

        var step = context.Node.Children.Count > 0 ? context.Node.Children[0] : null;
        var steps = 0L;
        while (step is not null)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (++steps > limit)
            {
                throw new WorkflowGraphException(
                    ExecutionErrorCodes.MaxStepsExceeded,
                    string.Create(CultureInfo.InvariantCulture, $"Flowchart '{context.Node.Id}' exceeded maxSteps ({limit})."));
            }

            step = await context.ExecuteStepAsync(step).ConfigureAwait(false);
        }

        return ActivityResult.Completed;
    }
}

/// <summary>
/// <c>Core.Decision</c>: a flowchart step that does nothing itself; its transitions carry the conditions (ADR-0037).
/// </summary>
public sealed class DecisionActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Decision"),
        "Decision",
        CoreActivities.ControlFlowCategory,
        "A flowchart branch point: does nothing itself; its transitions choose the next step.");

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult(ActivityResult.Completed);
    }
}
