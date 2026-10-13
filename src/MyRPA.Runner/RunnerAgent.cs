using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using MyRPA.Contracts.Orchestration;
using MyRPA.Core.Activities;
using MyRPA.Orchestration;
using MyRPA.Plugins;
using MyRPA.Workflow.Validation;

namespace MyRPA.Runner;

/// <summary>The Runner's exit code (set when it stops on its own, for example after its token was refused).</summary>
public sealed class RunnerExitCode
{
    /// <summary>0: stopped normally; 3: the Server refused the token.</summary>
    public int Value { get; set; }
}

/// <summary>The Runner's live state.</summary>
public sealed class RunnerState
{
    private volatile bool _paused;
    private volatile bool _registered;

    /// <summary>The administrator paused this Runner: it claims no jobs.</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>Registered with the Server.</summary>
    public bool Registered
    {
        get => _registered;
        set => _registered = value;
    }
}

/// <summary>
/// The Runner's main loop (ADR-0049): register, then heartbeat on one schedule and claim jobs on another while there is
/// free capacity. All traffic is outbound; the Runner never listens. On shutdown it stops claiming, gives back jobs it
/// has not started, cancels running ones and waits (up to the grace period) for their outcome to be reported.
/// </summary>
public sealed partial class RunnerAgent(
    RunnerOptions options,
    RunnerConnection connection,
    JobExecutor executor,
    RunnerState state,
    RunnerExitCode exitCode,
    IActivityCatalog catalog,
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    TimeProvider time,
    ILogger<RunnerAgent> logger) : BackgroundService
{
    /// <summary>What this Runner registers (from its own configuration and loaded plugins).</summary>
    public RunnerRegisterRequest Registration()
    {
        var registry = services.GetService<IPluginRegistry>();
        return new RunnerRegisterRequest
        {
            ProtocolVersion = RunnerProtocol.Version,
            RunnerVersion = typeof(RunnerAgent).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            PackageFormats = [WorkflowPackage.FormatName],
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            MachineName = Environment.MachineName,
            Service = WindowsServiceHelpers.IsWindowsService(),
            MaxConcurrency = options.MaxConcurrency,
            Plugins = [.. (registry?.Plugins ?? []).Select(p => new RunnerPluginInfo(p.Manifest.Id.Value, p.Manifest.Version.ToString()))],
            Activities = [.. catalog.Descriptors.Select(d => d.TypeName.Value)],
            PluginErrors = [.. (registry?.Diagnostics ?? []).Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())],
            ActiveJobs = executor.Held,
        };
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var registered = await RegisterAsync(stoppingToken).ConfigureAwait(false);
            LogRegistered(logger, registered.Name, registered.RunnerId, options.ServerUrl);
            state.Registered = true;
            var heartbeat = HeartbeatLoopAsync(TimeSpan.FromMilliseconds(Math.Max(1000, registered.HeartbeatIntervalMs)), stoppingToken);
            await ClaimLoopAsync(stoppingToken).ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }
        catch (RunnerAuthenticationException)
        {
            LogRefused(logger);
            exitCode.Value = 3;
            lifetime.StopApplication();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            state.Registered = false;
            await executor.ShutdownAsync(options.ShutdownGrace).ConfigureAwait(false);
        }
    }

    private async Task<RunnerRegisterResponse> RegisterAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                return await connection.RegisterAsync(Registration(), stoppingToken).ConfigureAwait(false);
            }
            catch (RunnerProtocolException ex)
            {
                LogUnreachable(logger, options.ServerUrl, ex.Message);
                await Task.Delay(delay, time, stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }
    }

    private async Task HeartbeatLoopAsync(TimeSpan interval, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var answer = await connection.HeartbeatAsync(new RunnerHeartbeatRequest { ActiveJobs = executor.Held }, stoppingToken).ConfigureAwait(false);
                    state.Paused = answer.Paused;
                    foreach (var jobId in answer.Cancel)
                    {
                        executor.Cancel(jobId);
                    }

                    foreach (var jobId in answer.Lost)
                    {
                        executor.Abandon(jobId);
                    }
                }
                catch (RunnerProtocolException ex)
                {
                    LogUnreachable(logger, options.ServerUrl, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (RunnerAuthenticationException)
        {
            LogRefused(logger);
            exitCode.Value = 3;
            lifetime.StopApplication();
        }
    }

    private async Task ClaimLoopAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (state.Paused || executor.ActiveCount >= options.MaxConcurrency)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), time, stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                var assignment = await connection.ClaimAsync(options.ClaimWait, stoppingToken).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(1);
                if (assignment is not null)
                {
                    executor.Start(assignment, stoppingToken);
                }
            }
            catch (RunnerProtocolException ex)
            {
                LogUnreachable(logger, options.ServerUrl, ex.Message);
                await Task.Delay(backoff, time, stoppingToken).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
    }

    [LoggerMessage(EventId = 6200, Level = LogLevel.Information, Message = "Registered as '{Name}' ({RunnerId}) with {Server}")]
    private static partial void LogRegistered(ILogger logger, string name, string runnerId, Uri server);

    [LoggerMessage(EventId = 6201, Level = LogLevel.Warning, Message = "The Server {Server} is not reachable: {Reason}; retrying")]
    private static partial void LogUnreachable(ILogger logger, Uri server, string reason);

    [LoggerMessage(EventId = 6202, Level = LogLevel.Error, Message = "The Server refused this Runner's token (wrong or revoked); the Runner stops")]
    private static partial void LogRefused(ILogger logger);
}
