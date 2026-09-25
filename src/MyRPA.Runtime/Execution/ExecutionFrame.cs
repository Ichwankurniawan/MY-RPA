using MyRPA.Core.Diagnostics;
using MyRPA.Workflow;

namespace MyRPA.Runtime.Execution;

/// <summary>Runtime state of one (possibly nested) workflow execution. Never shared between executions.</summary>
/// <param name="Workflow">The executing definition.</param>
/// <param name="Identity">Execution identity (without node).</param>
/// <param name="Location">Where <paramref name="Workflow"/> was loaded from.</param>
/// <param name="RootLocation">Where the entry workflow was loaded from (confinement root for invocations).</param>
/// <param name="Depth">Invocation depth (0 for the entry workflow).</param>
/// <param name="Services">The run's DI scope.</param>
/// <param name="Events">The run's execution-event sink (shared with invoked workflows; ADR-0023).</param>
/// <param name="Deadline">Earliest timeout of this execution and its invokers (set when execution starts).</param>
internal sealed record ExecutionFrame(
    WorkflowDefinition Workflow,
    ExecutionIdentity Identity,
    string? Location,
    string? RootLocation,
    int Depth,
    IServiceProvider Services,
    ExecutionEventSink Events,
    DateTimeOffset? Deadline = null);
