using MyRPA.Orchestration;

namespace MyRPA.Server;

/// <summary>
/// The orchestrator's clock (ADR-0049): on every tick it expires leases (requeue or interrupt) and fires due schedules.
/// A failure in one tick is logged and the next tick tries again; nothing here executes a workflow.
/// </summary>
internal sealed partial class OrchestratorBackground(Orchestrator orchestrator, ServerOptions options, TimeProvider time, ILogger<OrchestratorBackground> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.OrchestratorTick, time);
        try
        {
            do
            {
                try
                {
                    await orchestrator.SweepAsync(stoppingToken).ConfigureAwait(false);
                    await orchestrator.EvaluateSchedulesAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // A background tick must survive a transient store failure; the next tick retries.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogTickFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(EventId = 5100, Level = LogLevel.Warning, Message = "Orchestrator tick failed; retrying on the next tick")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}
