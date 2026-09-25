using Microsoft.Extensions.Logging;
using MyRPA.Workflow.Execution;

namespace MyRPA.Runtime.Execution;

/// <summary>
/// Delivers one run's execution events to the observer passed with the run request (ADR-0023). Shared by the run and the
/// workflows it invokes; never shared between runs.
/// </summary>
/// <remarks>
/// A throwing observer must not change the run's outcome: the failure is logged once and the observer is not called
/// again for this run. This is the one deliberate exception to "never swallow exceptions" in the engine: a consumer
/// that only observes must not break automation.
/// </remarks>
internal sealed partial class ExecutionEventSink
{
    private readonly ILogger _logger;
    private IExecutionObserver? _observer;

    /// <summary>Creates the sink.</summary>
    /// <param name="observer">The run's observer, or null (events are then not even created).</param>
    /// <param name="logger">Logs observer failures.</param>
    public ExecutionEventSink(IExecutionObserver? observer, ILogger logger)
    {
        _observer = observer;
        _logger = logger;
    }

    /// <summary>Whether an observer still wants events. Callers check this before allocating an event.</summary>
    public bool IsActive => Volatile.Read(ref _observer) is not null;

    /// <summary>Delivers an event; never throws.</summary>
    /// <param name="executionEvent">The event.</param>
    public void Emit(ExecutionEvent executionEvent)
    {
        if (Volatile.Read(ref _observer) is not { } observer)
        {
            return;
        }

        try
        {
            observer.OnEvent(executionEvent);
        }
#pragma warning disable CA1031 // ADR-0023: an observer failure (of any type, including cancellation) must never affect the run.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Volatile.Write(ref _observer, null);
            LogObserverFailed(_logger, executionEvent.GetType().Name, executionEvent.ExecutionId.ToString(), ex);
        }
    }

    [LoggerMessage(EventId = 3006, Level = LogLevel.Warning, Message = "The execution observer failed while handling {EventType} of execution {ExecutionId}; it receives no further events for this run")]
    private static partial void LogObserverFailed(ILogger logger, string eventType, string executionId, Exception exception);
}
