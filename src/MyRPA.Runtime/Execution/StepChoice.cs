using MyRPA.Workflow;

namespace MyRPA.Runtime.Execution;

/// <summary>
/// The transition a graph step chose while it ran (<c>IActivityContext.ChooseTransition</c>, ADR-0037): one per
/// <c>ExecuteStepAsync</c> call, read by the container's context when the step completes.
/// </summary>
internal sealed class StepChoice(NodeDefinition container)
{
    public NodeDefinition Container { get; } = container;

    public bool Made { get; set; }

    public NodeDefinition? Target { get; set; }
}
