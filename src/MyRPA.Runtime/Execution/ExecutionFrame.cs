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
/// <param name="Deadline">
/// Earliest timeout of this execution and its invokers (set when execution starts). In a debugged run it is net of the
/// time paused so far; <see cref="ClockDeadline"/> is the clock time.
/// </param>
/// <param name="Debug">The run's debugger link (ADR-0040); <see langword="null"/> when the run is not debugged.</param>
/// <param name="RootDepth">Debug nesting depth of this execution's root node (one deeper than the invoking node).</param>
internal sealed record ExecutionFrame(
    WorkflowDefinition Workflow,
    ExecutionIdentity Identity,
    string? Location,
    string? RootLocation,
    int Depth,
    IServiceProvider Services,
    ExecutionEventSink Events,
    DateTimeOffset? Deadline = null,
    DebugControl? Debug = null,
    int RootDepth = 0)
{
    /// <summary>The earliest timeout as a clock time (later than <see cref="Deadline"/> by the time paused so far).</summary>
    public DateTimeOffset? ClockDeadline => Debug is null ? Deadline : Deadline + Debug.Paused;
}
