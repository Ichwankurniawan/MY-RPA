using MyRPA.Core.Identifiers;
using MyRPA.Workflow.Expressions;

namespace MyRPA.Workflow;

/// <summary>
/// An arrow from a step of a graph container to a sibling step (format 1.1, ADR-0037). After the step completes, the
/// engine takes the first transition whose <see cref="When"/> is true or absent.
/// </summary>
public sealed class TransitionDefinition
{
    /// <summary>Creates a transition.</summary>
    /// <param name="to">The sibling step to run next.</param>
    /// <param name="when">Optional Boolean condition; <see langword="null"/> means always.</param>
    /// <param name="label">Optional text shown on the arrow.</param>
    public TransitionDefinition(NodeId to, WorkflowExpression? when = null, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(to);
        To = to;
        When = when;
        Label = label;
    }

    /// <summary>The sibling step to run next.</summary>
    public NodeId To { get; }

    /// <summary>The condition, or <see langword="null"/> when the transition is always taken.</summary>
    public WorkflowExpression? When { get; }

    /// <summary>Optional text shown on the arrow (designer only).</summary>
    public string? Label { get; }
}
