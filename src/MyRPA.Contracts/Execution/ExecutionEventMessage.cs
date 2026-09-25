using System.Text.Json.Serialization;

namespace MyRPA.Contracts.Execution;

/// <summary>Kinds of <see cref="ExecutionEventMessage"/>.</summary>
public static class ExecutionEventKinds
{
    /// <summary>An execution started (the run, or a workflow it invoked).</summary>
    public const string ExecutionStarted = "execution.started";

    /// <summary>A node started.</summary>
    public const string NodeStarted = "node.started";

    /// <summary>A node finished (<see cref="ExecutionEventMessage.Status"/>: Succeeded, Failed or Cancelled).</summary>
    public const string NodeCompleted = "node.completed";

    /// <summary>An execution finished. For the run itself (no parent) this is the last event of the stream.</summary>
    public const string ExecutionCompleted = "execution.completed";

    /// <summary>A workflow log entry (for example from <c>Core.Log</c>) or a warning written while the run executed.</summary>
    public const string Log = "log";

    /// <summary>
    /// Events between <see cref="ExecutionEventMessage.MissingFromSequence"/> and
    /// <see cref="ExecutionEventMessage.MissingToSequence"/> are no longer retained (the replay buffer is bounded).
    /// </summary>
    public const string Gap = "stream.gap";
}

/// <summary>
/// One event of a run, as streamed to clients (ADR-0023, ADR-0024). <see cref="Sequence"/> numbers start at 1 for each
/// run and increase without gaps, so a client resumes by asking for events after the last sequence it saw.
/// </summary>
/// <remarks>
/// Messages never carry variable values, arguments or outputs. Log messages carry what the workflow chose to log.
/// </remarks>
public sealed record ExecutionEventMessage
{
    /// <summary>Position in the run's event stream (1, 2, 3, …). 0 for <see cref="ExecutionEventKinds.Gap"/>.</summary>
    public required long Sequence { get; init; }

    /// <summary>One of <see cref="ExecutionEventKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The run (the host's handle, shared by every execution the run invokes).</summary>
    public required string RunId { get; init; }

    /// <summary>When the event occurred.</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>The execution (the run's own, or that of an invoked workflow).</summary>
    public string? ExecutionId { get; init; }

    /// <summary>The invoking execution, for invoked workflows.</summary>
    public string? ParentExecutionId { get; init; }

    /// <summary>The executing workflow (execution events).</summary>
    public string? WorkflowId { get; init; }

    /// <summary>The node (node and log events).</summary>
    public string? NodeId { get; init; }

    /// <summary>The node's activity type (node events).</summary>
    public string? ActivityType { get; init; }

    /// <summary>Outcome (completion events): Succeeded, Failed, Cancelled or TimedOut.</summary>
    public string? Status { get; init; }

    /// <summary>The failure (completion events that did not succeed).</summary>
    public ExecutionErrorMessage? Error { get; init; }

    /// <summary>Duration in milliseconds (execution completion).</summary>
    public double? DurationMs { get; init; }

    /// <summary>Log level (log events).</summary>
    public string? Level { get; init; }

    /// <summary>Log text (log events).</summary>
    public string? Message { get; init; }

    /// <summary>First sequence no longer retained (gap events).</summary>
    public long? MissingFromSequence { get; init; }

    /// <summary>Last sequence no longer retained (gap events).</summary>
    public long? MissingToSequence { get; init; }
}

/// <summary>A failure, as reported to clients.</summary>
/// <param name="Code">Error code, for example <c>MYRPA2001</c>.</param>
/// <param name="Message">Human-readable message.</param>
/// <param name="NodeId">The node where the failure originated.</param>
/// <param name="ActivityType">That node's activity type.</param>
/// <param name="ErrorType">Classification, for example <c>ElementNotFound</c>.</param>
public sealed record ExecutionErrorMessage(string Code, string Message, string? NodeId = null, string? ActivityType = null, string? ErrorType = null);

/// <summary>Source-generated JSON for the contracts: camelCase names, null members omitted.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ExecutionEventMessage))]
[JsonSerializable(typeof(IReadOnlyList<ExecutionEventMessage>))]
public sealed partial class ContractsJsonContext : JsonSerializerContext;
