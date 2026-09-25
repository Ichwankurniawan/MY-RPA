using MyRPA.Core.Activities;
using MyRPA.Core.Execution;
using MyRPA.Core.Identifiers;

namespace MyRPA.Workflow.Execution;

/// <summary>
/// Receives the execution events of one run (ADR-0023). Passed explicitly with <see cref="WorkflowRunRequest.Observer"/>;
/// never registered globally, so an observer only ever sees its own run and the workflows that run invokes.
/// </summary>
/// <remarks>
/// <para>
/// The engine calls <see cref="OnEvent"/> synchronously, on the executing flow, in execution order. Calls for one run
/// never overlap. Implementations must be fast and non-blocking (hand events to a queue); they are on the engine's path.
/// </para>
/// <para>
/// An observer cannot change a run's outcome: if <see cref="OnEvent"/> throws, the engine logs the failure and stops
/// calling this observer for the rest of the run, and the run continues unaffected.
/// </para>
/// </remarks>
public interface IExecutionObserver
{
    /// <summary>Handles one event.</summary>
    /// <param name="executionEvent">The event.</param>
    void OnEvent(ExecutionEvent executionEvent);
}

/// <summary>
/// An execution event. Events carry identifiers, activity types, statuses and errors only; never variable values,
/// arguments or outputs (those stay in <see cref="WorkflowExecutionResult"/>).
/// </summary>
/// <param name="ExecutionId">The execution the event belongs to (a nested invocation has its own).</param>
/// <param name="ParentExecutionId">The invoking execution, for nested invocations.</param>
/// <param name="CorrelationId">The run's correlation id (shared by nested invocations).</param>
/// <param name="Time">When the event occurred (engine clock).</param>
public abstract record ExecutionEvent(ExecutionId ExecutionId, ExecutionId? ParentExecutionId, CorrelationId CorrelationId, DateTimeOffset Time);

/// <summary>An execution (the run itself, or a nested invocation) started.</summary>
/// <param name="ExecutionId">The execution.</param>
/// <param name="ParentExecutionId">The invoking execution, for nested invocations.</param>
/// <param name="CorrelationId">The run's correlation id.</param>
/// <param name="Time">When it started.</param>
/// <param name="WorkflowId">The executing workflow.</param>
public sealed record ExecutionStarted(ExecutionId ExecutionId, ExecutionId? ParentExecutionId, CorrelationId CorrelationId, DateTimeOffset Time, WorkflowId WorkflowId)
    : ExecutionEvent(ExecutionId, ParentExecutionId, CorrelationId, Time);

/// <summary>A node started executing.</summary>
/// <param name="ExecutionId">The execution.</param>
/// <param name="ParentExecutionId">The invoking execution, for nested invocations.</param>
/// <param name="CorrelationId">The run's correlation id.</param>
/// <param name="Time">When it started.</param>
/// <param name="NodeId">The node.</param>
/// <param name="ActivityType">Its activity type.</param>
public sealed record NodeStarted(ExecutionId ExecutionId, ExecutionId? ParentExecutionId, CorrelationId CorrelationId, DateTimeOffset Time, NodeId NodeId, ActivityTypeName ActivityType)
    : ExecutionEvent(ExecutionId, ParentExecutionId, CorrelationId, Time);

/// <summary>
/// A node finished. <see cref="ExecutionStatus.Succeeded"/>, <see cref="ExecutionStatus.Failed"/> (with
/// <see cref="Error"/>; for a node that failed because a descendant failed, <see cref="ExecutionError.NodeId"/> names the
/// node where the failure originated) or <see cref="ExecutionStatus.Cancelled"/> (the run was cancelled or timed out).
/// </summary>
/// <param name="ExecutionId">The execution.</param>
/// <param name="ParentExecutionId">The invoking execution, for nested invocations.</param>
/// <param name="CorrelationId">The run's correlation id.</param>
/// <param name="Time">When it finished.</param>
/// <param name="NodeId">The node.</param>
/// <param name="ActivityType">Its activity type.</param>
/// <param name="Status">How it finished.</param>
/// <param name="Error">The failure, when <paramref name="Status"/> is <see cref="ExecutionStatus.Failed"/>.</param>
public sealed record NodeCompleted(
    ExecutionId ExecutionId,
    ExecutionId? ParentExecutionId,
    CorrelationId CorrelationId,
    DateTimeOffset Time,
    NodeId NodeId,
    ActivityTypeName ActivityType,
    ExecutionStatus Status,
    ExecutionError? Error)
    : ExecutionEvent(ExecutionId, ParentExecutionId, CorrelationId, Time);

/// <summary>An execution finished. This is always the last event of an execution.</summary>
/// <param name="ExecutionId">The execution.</param>
/// <param name="ParentExecutionId">The invoking execution, for nested invocations.</param>
/// <param name="CorrelationId">The run's correlation id.</param>
/// <param name="Time">When it finished.</param>
/// <param name="WorkflowId">The executed workflow.</param>
/// <param name="Status">The outcome (the same as <see cref="WorkflowExecutionResult.Status"/>).</param>
/// <param name="Error">The error, when the execution did not succeed.</param>
/// <param name="Duration">How long it ran.</param>
public sealed record ExecutionCompleted(
    ExecutionId ExecutionId,
    ExecutionId? ParentExecutionId,
    CorrelationId CorrelationId,
    DateTimeOffset Time,
    WorkflowId WorkflowId,
    ExecutionStatus Status,
    ExecutionError? Error,
    TimeSpan Duration)
    : ExecutionEvent(ExecutionId, ParentExecutionId, CorrelationId, Time);
