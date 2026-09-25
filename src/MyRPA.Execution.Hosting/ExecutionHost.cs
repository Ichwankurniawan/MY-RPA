using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MyRPA.Contracts.Execution;
using MyRPA.Core.Execution;
using MyRPA.Core.Identifiers;
using MyRPA.Workflow;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;

namespace MyRPA.Execution.Hosting;

/// <summary>What to run (the workflow itself is passed to <see cref="ExecutionHost.Start"/>).</summary>
public sealed record ExecutionStartRequest
{
    /// <summary>Input argument values by name.</summary>
    public IReadOnlyDictionary<string, object?> Arguments { get; init; } = new Dictionary<string, object?>();

    /// <summary>Maximum run time; null uses the engine default.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Where the workflow was loaded from (resolves and confines <c>Core.InvokeWorkflow</c>, ADR-0012).</summary>
    public string? Location { get; init; }
}

/// <summary>Where a run is in its life.</summary>
public enum ExecutionRunState
{
    /// <summary>Waiting for a free execution slot.</summary>
    Queued = 0,

    /// <summary>Executing.</summary>
    Running = 1,

    /// <summary>Finished (any outcome).</summary>
    Completed = 2,
}

/// <summary>
/// Starts and tracks workflow runs for a host (ADR-0022). Each run gets its own engine observer (ADR-0023), a bounded
/// replay buffer of sequenced events, its own log routing and its own cancellation; runs never share any of them.
/// </summary>
public sealed class ExecutionHost : IAsyncDisposable
{
    private readonly IWorkflowRunner _runner;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _time;
    private readonly ExecutionHostOptions _options;
    private readonly ExecutionRegistry _registry;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<string, Task> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _completedOrder = new();
    private int _disposed;

    internal ExecutionHost(IWorkflowRunner runner, IIdGenerator ids, TimeProvider time, ExecutionHostOptions options, ExecutionRegistry registry)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _slots = new SemaphoreSlim(options.MaxConcurrentExecutions, options.MaxConcurrentExecutions);
    }

    /// <summary>
    /// Starts a run and returns immediately. The run waits for a free slot when
    /// <see cref="ExecutionHostOptions.MaxConcurrentExecutions"/> runs are already executing.
    /// </summary>
    /// <param name="workflow">A validated workflow.</param>
    /// <param name="request">Arguments, timeout and location.</param>
    /// <returns>The run's handle. Its <see cref="ExecutionHandle.RunId"/> is a fresh correlation id.</returns>
    public ExecutionHandle Start(WorkflowDefinition workflow, ExecutionStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Always a fresh correlation id: it is the run's identity for event and log routing, so it must be unique.
        var record = new ExecutionRecord(_ids.NewCorrelationId(), _options.EventBufferCapacity);
        _registry.Add(record);

        // The engine may run long synchronous stretches (activities that never await); keep them off the caller's thread.
        _active[record.RunId] = Task.Run(() => RunAsync(record, workflow, request));
        return new ExecutionHandle(record);
    }

    /// <summary>Finds a run that is active or still retained.</summary>
    /// <param name="runId">The run id.</param>
    /// <param name="handle">Its handle.</param>
    public bool TryGet(string runId, [NotNullWhen(true)] out ExecutionHandle? handle)
    {
        ArgumentNullException.ThrowIfNull(runId);
        if (_registry.TryGet(runId, out var record))
        {
            handle = new ExecutionHandle(record);
            return true;
        }

        handle = null;
        return false;
    }

    /// <summary>Requests cancellation of a queued or running run (cooperative, like the CLI's Ctrl+C).</summary>
    /// <param name="runId">The run id.</param>
    /// <returns>Whether the run is known and was not yet completed.</returns>
    public bool Cancel(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        if (!_registry.TryGet(runId, out var record) || record.IsCompleted)
        {
            return false;
        }

        try
        {
            record.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completed between the check and the cancel; nothing to cancel.
            return false;
        }

        return true;
    }

    /// <summary>Cancels every run and waits for them to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var runId in _active.Keys)
        {
            Cancel(runId);
        }

        await Task.WhenAll(_active.Values).ConfigureAwait(false);
        _slots.Dispose();
    }

    private async Task RunAsync(ExecutionRecord record, WorkflowDefinition workflow, ExecutionStartRequest request)
    {
        WorkflowExecutionResult? result = null;
        try
        {
            try
            {
                await _slots.WaitAsync(record.Cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (record.Cancellation.IsCancellationRequested)
            {
                result = CancelledBeforeStart(record, workflow);
                return;
            }

            try
            {
                record.MarkRunning();
                result = await _runner.RunAsync(
                    workflow,
                    new WorkflowRunRequest
                    {
                        Arguments = request.Arguments,
                        CorrelationId = record.CorrelationId,
                        Timeout = request.Timeout,
                        Location = request.Location,
                        Observer = record,
                    },
                    record.Cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                _slots.Release();
            }
        }
#pragma warning disable CA1031 // Host boundary: the run must end (readers and awaiters complete); the failure goes to Completion.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            record.CompleteWithHostFailure(_time.GetUtcNow(), ex.Message);
            record.Result.TrySetException(ex);
        }
        finally
        {
            if (result is not null)
            {
                // The engine always emits the terminal event; this only covers runs that never reached the engine.
                record.CompleteFrom(result);
                record.Result.TrySetResult(result);
            }

            record.MarkCompleted();
            _active.TryRemove(record.RunId, out _);
            Retain(record);
        }
    }

    private WorkflowExecutionResult CancelledBeforeStart(ExecutionRecord record, WorkflowDefinition workflow) =>
        new(
            _ids.NewExecutionId(),
            record.CorrelationId,
            workflow.Id,
            ExecutionStatus.Cancelled,
            WorkflowValues.Dictionary([]),
            new ExecutionError(ExecutionErrorCodes.Cancelled, "The run was cancelled before it started.", ErrorType: "Cancelled"),
            _time.GetUtcNow(),
            TimeSpan.Zero);

    private void Retain(ExecutionRecord record)
    {
        _completedOrder.Enqueue(record.RunId);
        while (_completedOrder.Count > _options.MaxRetainedExecutions && _completedOrder.TryDequeue(out var oldest))
        {
            if (_registry.Remove(oldest, out var evicted))
            {
                evicted.Dispose();
            }
        }
    }
}

/// <summary>A run: its identity, state, result and event stream.</summary>
public sealed class ExecutionHandle
{
    private readonly ExecutionRecord _record;

    internal ExecutionHandle(ExecutionRecord record) => _record = record;

    /// <summary>The run id (equals the run's correlation id).</summary>
    public string RunId => _record.RunId;

    /// <summary>The run's correlation id (shared by the workflows it invokes).</summary>
    public CorrelationId CorrelationId => _record.CorrelationId;

    /// <summary>Queued, running or completed.</summary>
    public ExecutionRunState State => _record.State;

    /// <summary>The last event sequence number so far.</summary>
    public long LastSequence => _record.LastSequence;

    /// <summary>Completes with the engine's result (a cancelled result if the run was cancelled while queued).</summary>
    public Task<WorkflowExecutionResult> Completion => _record.Result.Task;

    /// <summary>
    /// The run's events after <paramref name="afterSequence"/> (0 for all), then new events as they happen, until the
    /// run's terminal <see cref="ExecutionEventKinds.ExecutionCompleted"/> event. If events after
    /// <paramref name="afterSequence"/> are no longer retained, a <see cref="ExecutionEventKinds.Gap"/> event says which.
    /// </summary>
    /// <param name="afterSequence">The last sequence the reader has seen.</param>
    /// <param name="cancellationToken">Stops reading (does not cancel the run).</param>
    public async IAsyncEnumerable<ExecutionEventMessage> ReadEventsAsync(long afterSequence = 0, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        var after = afterSequence;
        while (true)
        {
            var (events, firstRetained, completed, changed) = _record.Read(after);
            if (events.Count > 0 && after + 1 < firstRetained)
            {
                yield return new ExecutionEventMessage
                {
                    Sequence = 0,
                    Kind = ExecutionEventKinds.Gap,
                    RunId = RunId,
                    Time = events[0].Time,
                    MissingFromSequence = after + 1,
                    MissingToSequence = firstRetained - 1,
                };
            }

            foreach (var message in events)
            {
                yield return message;
                after = message.Sequence;
            }

            if (completed)
            {
                yield break;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Limits of an <see cref="ExecutionHost"/>.</summary>
public sealed class ExecutionHostOptions
{
    /// <summary>Runs executing at the same time; further runs wait (default 4).</summary>
    public int MaxConcurrentExecutions { get; set; } = 4;

    /// <summary>Events retained per run for replay; older ones are dropped and reported as a gap (default 10,000).</summary>
    public int EventBufferCapacity { get; set; } = 10_000;

    /// <summary>Completed runs kept for status and replay; the oldest are forgotten (default 100).</summary>
    public int MaxRetainedExecutions { get; set; } = 100;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrentExecutions, 1, nameof(MaxConcurrentExecutions));
        ArgumentOutOfRangeException.ThrowIfLessThan(EventBufferCapacity, 2, nameof(EventBufferCapacity));
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRetainedExecutions, nameof(MaxRetainedExecutions));
    }
}
