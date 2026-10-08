using System.Globalization;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;

namespace MyRPA.Activities.BuiltIn;

/// <summary>
/// <c>Core.StateMachine</c>: a graph container of <c>Core.State</c> steps (ADR-0037). Runs its first state, then the state
/// each one chooses, until a final state completes. Exceeding <c>maxSteps</c> fails the node with MYRPA2010.
/// </summary>
public sealed class StateMachineActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("StateMachine"),
        "State Machine",
        CoreActivities.ControlFlowCategory,
        "Runs states from the first one; each state's transitions choose the next; ends after a final state.",
        [new("maxSteps", ActivityPropertyKind.Expression, isRequired: false, "Optional safety limit (Int, default 10,000) on the states run; exceeding it fails the state machine.")],
        allowsChildren: true,
        childLayout: ActivityChildLayout.Graph);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var limit = context.HasProperty("maxSteps") ? context.EvaluateInt("maxSteps") : FlowchartActivity.DefaultMaxSteps;
        if (limit < 0)
        {
            throw new InvalidOperationException("maxSteps must not be negative.");
        }

        var state = context.Node.Children.Count > 0 ? context.Node.Children[0] : null;
        var steps = 0L;
        while (state is not null)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (++steps > limit)
            {
                throw new WorkflowGraphException(
                    ExecutionErrorCodes.MaxStepsExceeded,
                    string.Create(CultureInfo.InvariantCulture, $"State machine '{context.Node.Id}' exceeded maxSteps ({limit})."));
            }

            // A state that is not final fails itself when no transition is taken (MYRPA2011), so null means a final state.
            state = await context.ExecuteStepAsync(state).ConfigureAwait(false);
        }

        return ActivityResult.Completed;
    }
}

/// <summary>
/// <c>Core.State</c>: a state of a <c>Core.StateMachine</c> (ADR-0037). Runs <c>entry</c>; then, unless it is final, its
/// transitions choose the next state (conditions only) and none taken fails with MYRPA2011; then runs <c>exit</c>.
/// </summary>
public sealed class StateActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("State"),
        "State",
        CoreActivities.ControlFlowCategory,
        "A state of a state machine: runs entry, its transitions choose the next state, then runs exit. A final state ends the machine.",
        [new("final", ActivityPropertyKind.Expression, isRequired: false, "Whether this state ends the state machine (Boolean, default false).")],
        slots: [new("entry", description: "Runs when the state is entered."), new("exit", description: "Runs when the state is left, after the next state is chosen.")]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var node = context.Node;
        if (node.Slots.TryGetValue("entry", out var entry))
        {
            await context.ExecuteAsync(entry).ConfigureAwait(false);
        }

        var final = context.HasProperty("final") && context.EvaluateBoolean("final");
        if (final && node.Transitions.Count > 0)
        {
            throw new WorkflowGraphException(ExecutionErrorCodes.StateMachineStuck, $"State '{node.Id}' is final, so it cannot have transitions.");
        }

        if (!final && context.ChooseTransition() is null)
        {
            throw new WorkflowGraphException(ExecutionErrorCodes.StateMachineStuck, $"No transition from state '{node.Id}' was taken, and it is not a final state.");
        }

        if (node.Slots.TryGetValue("exit", out var exit))
        {
            await context.ExecuteAsync(exit).ConfigureAwait(false);
        }

        return ActivityResult.Completed;
    }
}
