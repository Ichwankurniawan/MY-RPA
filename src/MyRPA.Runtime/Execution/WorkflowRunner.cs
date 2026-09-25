using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyRPA.Core.Diagnostics;
using MyRPA.Core.Execution;
using MyRPA.Core.Identifiers;
using MyRPA.Runtime.Diagnostics;
using MyRPA.Workflow;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;

namespace MyRPA.Runtime.Execution;

/// <summary>
/// The workflow engine (ADR-0010). Stateless singleton: all per-run state lives in <see cref="ExecutionFrame"/> and
/// <see cref="VariableScope"/>, and every top-level run gets its own DI scope.
/// </summary>
public sealed partial class WorkflowRunner : IWorkflowRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IActivityFactory _activities;
    private readonly IExecutionScopeFactory _scopes;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _time;
    private readonly WorkflowRuntimeOptions _options;
    private readonly ILogger<WorkflowRunner> _logger;

    /// <summary>Creates the runner.</summary>
    /// <param name="scopeFactory">Creates the per-run DI scope.</param>
    /// <param name="activities">Creates activity instances.</param>
    /// <param name="scopes">Logging/tracing scopes.</param>
    /// <param name="ids">Identifier generator.</param>
    /// <param name="time">Clock (timeouts, timestamps).</param>
    /// <param name="options">Engine limits.</param>
    /// <param name="logger">Logger.</param>
    public WorkflowRunner(
        IServiceScopeFactory scopeFactory,
        IActivityFactory activities,
        IExecutionScopeFactory scopes,
        IIdGenerator ids,
        TimeProvider time,
        WorkflowRuntimeOptions options,
        ILogger<WorkflowRunner> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _activities = activities ?? throw new ArgumentNullException(nameof(activities));
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<WorkflowExecutionResult> RunAsync(WorkflowDefinition workflow, WorkflowRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(request);

        var identity = new ExecutionIdentity(_ids.NewExecutionId(), request.CorrelationId ?? _ids.NewCorrelationId(), workflow.Id);
        var scope = _scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var events = new ExecutionEventSink(request.Observer, _logger);
            var frame = new ExecutionFrame(workflow, identity, request.Location, request.Location, Depth: 0, scope.ServiceProvider, events);
            return await ExecuteAsync(frame, request.Arguments, request.Timeout ?? _options.DefaultTimeout, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async ValueTask ExecuteNodeAsync(ExecutionFrame frame, NodeDefinition node, VariableScope variables, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = frame.Identity.ForNode(node.Id);
        using var observed = _scopes.Begin(identity, node.Type.Value, [new(DiagnosticNames.ActivityTypeKey, node.Type.Value)]);
        if (frame.Events.IsActive)
        {
            frame.Events.Emit(new NodeStarted(identity.ExecutionId, identity.ParentExecutionId, identity.CorrelationId, _time.GetUtcNow(), node.Id, node.Type));
        }

        try
        {
            // One instance per invocation, owned and disposed here (ADR-0013): nothing retains it for the rest of the run.
            var activity = _activities.Create(node.Type, frame.Services);
            try
            {
                var context = new ActivityContext(this, frame, node, identity, variables, _time, cancellationToken);
                await activity.ExecuteAsync(context).ConfigureAwait(false);
            }
            catch (Exception original)
            {
                await DisposeAfterFailureAsync(activity, node, original).ConfigureAwait(false);
                throw;
            }

            // Disposal failures after a successful invocation fail the node like any other activity exception.
            await DisposeActivityAsync(activity).ConfigureAwait(false);
            observed.Complete(ExecutionStatus.Succeeded);
            NodeFinished(frame, identity, node, ExecutionStatus.Succeeded, null);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            observed.Complete(ExecutionStatus.Cancelled, ex);
            NodeFinished(frame, identity, node, ExecutionStatus.Cancelled, null);
            throw;
        }
        catch (WorkflowActivityException ex)
        {
            // Already attributed to the node where it originated; mark this ancestor as failed and propagate.
            observed.Complete(ExecutionStatus.Failed, ex);
            NodeFinished(frame, identity, node, ExecutionStatus.Failed, ex.ToExecutionError());
            throw;
        }
        catch (Exception ex)
        {
            var failure = new WorkflowActivityException(node.Id, node.Type, ex);
            LogNodeFailed(_logger, node.Id.Value, node.Type.Value, ex.Message, ex);
            observed.Complete(ExecutionStatus.Failed, failure);
            NodeFinished(frame, identity, node, ExecutionStatus.Failed, failure.ToExecutionError());
            throw failure;
        }
    }

    private void NodeFinished(ExecutionFrame frame, ExecutionIdentity identity, NodeDefinition node, ExecutionStatus status, ExecutionError? error)
    {
        if (frame.Events.IsActive)
        {
            frame.Events.Emit(new NodeCompleted(identity.ExecutionId, identity.ParentExecutionId, identity.CorrelationId, _time.GetUtcNow(), node.Id, node.Type, status, error));
        }
    }

    private static ValueTask DisposeActivityAsync(IActivity activity)
    {
        switch (activity)
        {
            case IAsyncDisposable asyncDisposable:
                return asyncDisposable.DisposeAsync();
            case IDisposable disposable:
                disposable.Dispose();
                return ValueTask.CompletedTask;
            default:
                return ValueTask.CompletedTask;
        }
    }

    private async ValueTask DisposeAfterFailureAsync(IActivity activity, NodeDefinition node, Exception original)
    {
        try
        {
            await DisposeActivityAsync(activity).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The original failure (or cancellation) must win; a disposal failure is only logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogDisposeFailed(_logger, node.Id.Value, node.Type.Value, original.GetType().Name, ex);
        }
    }

    internal async ValueTask<WorkflowExecutionResult> InvokeAsync(
        ExecutionFrame parent,
        string reference,
        IReadOnlyDictionary<string, object?> arguments,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        if (parent.Depth + 1 > _options.MaxInvocationDepth)
        {
            throw new WorkflowInvocationException(
                ExecutionErrorCodes.InvocationDepthExceeded,
                $"Cannot invoke '{reference}': the maximum invocation depth of {_options.MaxInvocationDepth} was exceeded.");
        }

        if (parent.Location is null || parent.RootLocation is null)
        {
            throw new WorkflowInvocationException(
                ExecutionErrorCodes.WorkflowNotResolved,
                $"Cannot invoke '{reference}': the running workflow has no location to resolve references from.");
        }

        var resolver = parent.Services.GetService<IWorkflowResolver>()
            ?? throw new WorkflowInvocationException(ExecutionErrorCodes.WorkflowNotResolved, $"Cannot invoke '{reference}': no workflow resolver is registered.");

        var resolution = await resolver.ResolveAsync(reference, parent.Location, parent.RootLocation, cancellationToken).ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            var details = resolution.Diagnostics.Where(d => d.Severity == Workflow.Validation.DiagnosticSeverity.Error).Select(d => d.ToString());
            throw new WorkflowInvocationException(
                ExecutionErrorCodes.WorkflowNotResolved,
                string.Join(Environment.NewLine, [$"Cannot invoke '{reference}': {resolution.Error}", .. details]));
        }

        var childIdentity = parent.Identity.ForChildExecution(_ids.NewExecutionId(), resolution.Workflow.Id);
        var child = new ExecutionFrame(
            resolution.Workflow, childIdentity, resolution.Location, parent.RootLocation, parent.Depth + 1, parent.Services, parent.Events, parent.Deadline);
        var result = await ExecuteAsync(child, arguments, timeout, cancellationToken).ConfigureAwait(false);

        // The child reports "Cancelled" when our token was cancelled; propagate that as cancellation of this execution.
        if (result.Status == ExecutionStatus.Cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return result;
    }

    private async ValueTask<WorkflowExecutionResult> ExecuteAsync(
        ExecutionFrame frame,
        IReadOnlyDictionary<string, object?> arguments,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var workflow = frame.Workflow;
        var startedAt = _time.GetUtcNow();
        var startTimestamp = _time.GetTimestamp();
        frame = frame with { Deadline = EarliestDeadline(frame.Deadline, timeout is { } limit ? startedAt + limit : null) };
        using var observed = _scopes.Begin(frame.Identity, DiagnosticNames.WorkflowExecuteOperation);
        LogStarted(_logger, workflow.Id.Value, workflow.Name, workflow.Version);
        var identity = frame.Identity;
        if (frame.Events.IsActive)
        {
            frame.Events.Emit(new ExecutionStarted(identity.ExecutionId, identity.ParentExecutionId, identity.CorrelationId, startedAt, workflow.Id));
        }

        WorkflowExecutionResult Finish(ExecutionStatus status, IReadOnlyDictionary<string, object?>? outputs, ExecutionError? error, Exception? exception)
        {
            var duration = _time.GetElapsedTime(startTimestamp);
            observed.Complete(status, exception);
            LogCompleted(_logger, workflow.Id.Value, status, (long)duration.TotalMilliseconds);
            if (frame.Events.IsActive)
            {
                frame.Events.Emit(new ExecutionCompleted(identity.ExecutionId, identity.ParentExecutionId, identity.CorrelationId, _time.GetUtcNow(), workflow.Id, status, error, duration));
            }

            return new WorkflowExecutionResult(
                frame.Identity.ExecutionId,
                frame.Identity.CorrelationId,
                workflow.Id,
                status,
                outputs ?? WorkflowValues.Dictionary([]),
                error,
                startedAt,
                duration,
                frame.Identity.ParentExecutionId);
        }

        if (!TryBindArguments(workflow, arguments, out var values, out var argumentError))
        {
            LogArgumentsRejected(_logger, workflow.Id.Value, argumentError);
            return Finish(ExecutionStatus.Failed, null, new ExecutionError(ExecutionErrorCodes.InvalidArguments, argumentError, ErrorType: "Arguments"), null);
        }

        var variables = VariableScope.CreateRoot(workflow, values, _time);
        using var timeoutSource = timeout is { } t ? new CancellationTokenSource(t, _time) : null;
        using var linked = timeoutSource is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await ExecuteNodeAsync(frame, workflow.Root, variables, linked.Token).ConfigureAwait(false);
            return Finish(ExecutionStatus.Succeeded, variables.GetOutputs(), null, null);
        }
        catch (OperationCanceledException ex) when (timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            var message = FormattableString.Invariant($"The workflow did not complete within {timeout!.Value.TotalMilliseconds:0} ms.");
            return Finish(ExecutionStatus.TimedOut, null, new ExecutionError(ExecutionErrorCodes.TimedOut, message, ErrorType: "Timeout"), ex);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            return Finish(ExecutionStatus.Cancelled, null, new ExecutionError(ExecutionErrorCodes.Cancelled, "The workflow was cancelled.", ErrorType: "Cancelled"), ex);
        }
        catch (WorkflowActivityException ex)
        {
            LogFailed(_logger, workflow.Id.Value, ex.NodeId.Value, ex.ErrorMessage, ex);
            return Finish(ExecutionStatus.Failed, null, ex.ToExecutionError(), ex);
        }
    }

    private static DateTimeOffset? EarliestDeadline(DateTimeOffset? inherited, DateTimeOffset? own) =>
        (inherited, own) switch
        {
            ({ } a, { } b) => a <= b ? a : b,
            _ => inherited ?? own,
        };

    private static bool TryBindArguments(
        WorkflowDefinition workflow,
        IReadOnlyDictionary<string, object?> supplied,
        out Dictionary<string, object?> values,
        out string error)
    {
        values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var problems = new List<string>();
        var declared = workflow.Arguments.ToDictionary(a => a.Name, StringComparer.Ordinal);

        foreach (var (name, value) in supplied)
        {
            if (!declared.TryGetValue(name, out var argument))
            {
                problems.Add($"Unknown argument '{name}'.");
            }
            else if (!argument.IsInput)
            {
                problems.Add($"Argument '{name}' is Out and cannot be supplied.");
            }
            else if (!WorkflowValues.TryNormalize(value, out var canonical, out var normalizeError))
            {
                problems.Add($"Argument '{name}': {normalizeError}");
            }
            else if (!WorkflowValues.TryConvert(canonical, argument.Type, out var converted, out var convertError))
            {
                problems.Add($"Argument '{name}': {convertError}");
            }
            else
            {
                values[name] = converted;
            }
        }

        foreach (var argument in workflow.Arguments.Where(a => a.IsInput && !supplied.ContainsKey(a.Name)))
        {
            if (argument.IsRequired)
            {
                problems.Add($"Required argument '{argument.Name}' was not supplied.");
            }
            else
            {
                values[argument.Name] = argument.DefaultValue;
            }
        }

        error = string.Join(" ", problems);
        return problems.Count == 0;
    }

    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "Workflow {WorkflowId} ({WorkflowName} {WorkflowVersion}) started")]
    private static partial void LogStarted(ILogger logger, string workflowId, string workflowName, string workflowVersion);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Workflow {WorkflowId} finished with status {Status} in {DurationMs} ms")]
    private static partial void LogCompleted(ILogger logger, string workflowId, ExecutionStatus status, long durationMs);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Error, Message = "Workflow {WorkflowId} failed at node {NodeId}: {ErrorMessage}")]
    private static partial void LogFailed(ILogger logger, string workflowId, string nodeId, string errorMessage, Exception exception);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning, Message = "Workflow {WorkflowId} rejected its arguments: {Reason}")]
    private static partial void LogArgumentsRejected(ILogger logger, string workflowId, string reason);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Debug, Message = "Node {NodeId} ({ActivityType}) failed: {ErrorMessage}")]
    private static partial void LogNodeFailed(ILogger logger, string nodeId, string activityType, string errorMessage, Exception exception);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Warning, Message = "Disposing node {NodeId} ({ActivityType}) failed after {OriginalError}; the original outcome is kept")]
    private static partial void LogDisposeFailed(ILogger logger, string nodeId, string activityType, string originalError, Exception exception);
}
