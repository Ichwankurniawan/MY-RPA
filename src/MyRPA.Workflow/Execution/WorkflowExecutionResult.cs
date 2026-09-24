using MyRPA.Core.Execution;
using MyRPA.Core.Identifiers;

namespace MyRPA.Workflow.Execution;

/// <summary>The outcome of one workflow execution (PRD "WorkflowExecution").</summary>
public sealed class WorkflowExecutionResult
{
    /// <summary>Creates a result.</summary>
    /// <param name="executionId">Execution id.</param>
    /// <param name="correlationId">Correlation id.</param>
    /// <param name="workflowId">Executed workflow.</param>
    /// <param name="status">Terminal status.</param>
    /// <param name="outputs">Out/InOut argument values (canonical).</param>
    /// <param name="error">Error details; required unless <paramref name="status"/> is <see cref="ExecutionStatus.Succeeded"/>.</param>
    /// <param name="startedAt">Start time.</param>
    /// <param name="duration">Elapsed time.</param>
    /// <param name="parentExecutionId">Invoking execution for nested workflows.</param>
    public WorkflowExecutionResult(
        ExecutionId executionId,
        CorrelationId correlationId,
        WorkflowId workflowId,
        ExecutionStatus status,
        IReadOnlyDictionary<string, object?> outputs,
        ExecutionError? error,
        DateTimeOffset startedAt,
        TimeSpan duration,
        ExecutionId? parentExecutionId = null)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        ArgumentNullException.ThrowIfNull(correlationId);
        ArgumentNullException.ThrowIfNull(workflowId);
        ArgumentNullException.ThrowIfNull(outputs);
        if ((status == ExecutionStatus.Succeeded) != (error is null))
        {
            throw new ArgumentException("An error is required exactly when the status is not Succeeded.", nameof(error));
        }

        ExecutionId = executionId;
        CorrelationId = correlationId;
        WorkflowId = workflowId;
        Status = status;
        Outputs = outputs;
        Error = error;
        StartedAt = startedAt;
        Duration = duration;
        ParentExecutionId = parentExecutionId;
    }

    /// <summary>Execution id.</summary>
    public ExecutionId ExecutionId { get; }

    /// <summary>Correlation id.</summary>
    public CorrelationId CorrelationId { get; }

    /// <summary>Executed workflow.</summary>
    public WorkflowId WorkflowId { get; }

    /// <summary>Invoking execution for nested workflows.</summary>
    public ExecutionId? ParentExecutionId { get; }

    /// <summary>Terminal status.</summary>
    public ExecutionStatus Status { get; }

    /// <summary>Out/InOut argument values after a successful run (empty otherwise).</summary>
    public IReadOnlyDictionary<string, object?> Outputs { get; }

    /// <summary>Error details when not succeeded.</summary>
    public ExecutionError? Error { get; }

    /// <summary>Start time.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Elapsed time.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Whether the run succeeded.</summary>
    public bool Succeeded => Status == ExecutionStatus.Succeeded;
}

/// <summary>Why an execution did not succeed.</summary>
/// <param name="Code">Stable code, see <see cref="ExecutionErrorCodes"/>.</param>
/// <param name="Message">Human-readable message.</param>
/// <param name="NodeId">The node that failed, when attributable.</param>
/// <param name="ActivityType">The failing node's activity type.</param>
/// <param name="ErrorType">Exception type name or workflow error kind.</param>
public sealed record ExecutionError(string Code, string Message, string? NodeId = null, string? ActivityType = null, string? ErrorType = null);

/// <summary>Stable execution error codes.</summary>
public static class ExecutionErrorCodes
{
    /// <summary>An activity threw an unexpected exception.</summary>
    public const string ActivityFailed = "MYRPA2001";

    /// <summary>A <c>Core.Throw</c> activity raised an error.</summary>
    public const string WorkflowThrow = "MYRPA2002";

    /// <summary>An expression failed to evaluate (type error, unknown key, overflow, ...).</summary>
    public const string ExpressionFailed = "MYRPA2003";

    /// <summary>Run arguments were unknown, missing or of the wrong type.</summary>
    public const string InvalidArguments = "MYRPA2004";

    /// <summary>A timeout elapsed.</summary>
    public const string TimedOut = "MYRPA2005";

    /// <summary>The run was cancelled.</summary>
    public const string Cancelled = "MYRPA2006";

    /// <summary>An invoked workflow did not succeed.</summary>
    public const string InvokedWorkflowFailed = "MYRPA2007";

    /// <summary>The maximum invocation depth was exceeded.</summary>
    public const string InvocationDepthExceeded = "MYRPA2008";

    /// <summary>An invoked workflow could not be resolved or is invalid.</summary>
    public const string WorkflowNotResolved = "MYRPA2009";
}
