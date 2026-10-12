using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MyRPA.Activities;
using MyRPA.Contracts.Orchestration;
using MyRPA.Core.Diagnostics;
using MyRPA.Execution.Hosting;
using MyRPA.Plugins;
using MyRPA.Runtime;
using MyRPA.Storage;

namespace MyRPA.Runner;

/// <summary>
/// The Runner's composition root (ADR-0049): the same engine, built-in activities, file resolver, plugin host and
/// execution hosting as the CLI and the Server, plus the Runner's connection and agent. Runs in a console or, when
/// started by the Windows Service Control Manager, as a service.
/// </summary>
public static class RunnerApplication
{
    /// <summary>Command-line usage.</summary>
    public const string Usage = "myrpa-runner run --config <runner.json>   (or: myrpa-runner check --config <runner.json>)";

    /// <summary>Runs the Runner (or checks its configuration and connection).</summary>
    /// <returns>0 on a normal stop; 2 usage or configuration error; 3 token refused; 4 Server unreachable; 5 plugin failure.</returns>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args.Length != 3 || args[0] is not ("run" or "check") || args[1] != "--config")
        {
            await error.WriteLineAsync($"Usage: {Usage}").ConfigureAwait(false);
            return 2;
        }

        if (RunnerConfiguration.Load(args[2], Environment.GetEnvironmentVariable, out var configError) is not { } options)
        {
            await error.WriteLineAsync($"Error: {configError}").ConfigureAwait(false);
            return 2;
        }

        PluginSet? plugins = null;
        try
        {
            if (options.PluginDirectories.Count > 0 || options.PluginConfiguration is not null)
            {
                try
                {
                    var hostOptions = await PluginConfigurationFile.CreateHostOptionsAsync(options.PluginDirectories, options.PluginConfiguration, cancellationToken).ConfigureAwait(false);
                    plugins = await PluginLoader.LoadAsync(hostOptions, cancellationToken).ConfigureAwait(false);
                }
                catch (PluginConfigurationException ex)
                {
                    await error.WriteLineAsync($"Error: {ex.Message}").ConfigureAwait(false);
                    return 5;
                }

                foreach (var diagnostic in plugins.Diagnostics)
                {
                    await error.WriteLineAsync(diagnostic.ToString()).ConfigureAwait(false);
                }

                if (plugins.HasRequiredFailures)
                {
                    await error.WriteLineAsync("Error: plugins failed to load; the Runner was not started.").ConfigureAwait(false);
                    return 5;
                }
            }

            using var host = BuildHost(options, plugins);
            if (args[0] == "check")
            {
                return await CheckAsync(host, options, output, error, cancellationToken).ConfigureAwait(false);
            }

            await output.WriteLineAsync($"MyRPA Runner: {options}").ConfigureAwait(false);
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
            return host.Services.GetRequiredService<RunnerExitCode>().Value;
        }
        finally
        {
            if (plugins is not null)
            {
                await plugins.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Builds the Runner host. Exposed so tests compose exactly what the Runner composes.</summary>
    /// <param name="options">Configuration.</param>
    /// <param name="plugins">Loaded plugins (the caller disposes them after the host).</param>
    /// <param name="time">Clock (tests); the system clock otherwise.</param>
    public static IHost BuildHost(RunnerOptions options, PluginSet? plugins, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "myrpa-runner",
            // Configuration comes from the install directory, never from the caller's working directory.
            ContentRootPath = AppContext.BaseDirectory,
            Args = [],
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(c => c.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("MyRPA.Runner", LogLevel.Information);

        // Workflow log messages reach the per-run log router (and from there the Server's job history), not the console.
        builder.Logging.AddFilter(DiagnosticNames.WorkflowLogCategory, LogLevel.Information);
        builder.Logging.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(DiagnosticNames.WorkflowLogCategory, LogLevel.Warning);
        builder.Services.AddWindowsService(service => service.ServiceName = "LaconiRunner");
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = options.ShutdownGrace + TimeSpan.FromSeconds(10));

        var services = builder.Services;
        if (time is not null)
        {
            services.AddSingleton(time);
        }

        services.AddMyRpaRuntime();
        services.AddMyRpaActivities();
        services.AddMyRpaStorage();
        if (plugins is not null)
        {
            services.AddMyRpaPlugins(plugins);
        }

        services.AddMyRpaExecutionHosting(hosting => hosting.MaxConcurrentExecutions = options.MaxConcurrency);
        services.AddSingleton(options);
        services.AddSingleton<RunnerConnection>();
        services.AddSingleton<JobExecutor>();
        services.AddSingleton<RunnerState>();
        services.AddSingleton<RunnerExitCode>();
        services.AddSingleton<RunnerAgent>();
        services.AddHostedService(provider => provider.GetRequiredService<RunnerAgent>());
        var host = builder.Build();
        plugins?.VerifyProviders(host.Services);
        return host;
    }

    private static async Task<int> CheckAsync(IHost host, RunnerOptions options, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var registration = host.Services.GetRequiredService<RunnerAgent>().Registration();
        await output.WriteLineAsync($"Configuration: {options}").ConfigureAwait(false);
        await output.WriteLineAsync($"Activities: {registration.Activities.Count}; plugins: {string.Join(", ", registration.Plugins.Select(p => $"{p.Id} {p.Version}"))}").ConfigureAwait(false);
        try
        {
            await host.Services.GetRequiredService<RunnerConnection>().HeartbeatAsync(new RunnerHeartbeatRequest(), cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"Server {options.ServerUrl}: reachable, token accepted.").ConfigureAwait(false);
            return 0;
        }
        catch (RunnerAuthenticationException ex)
        {
            await error.WriteLineAsync($"Error: {ex.Message}").ConfigureAwait(false);
            return 3;
        }
        catch (RunnerProtocolException ex)
        {
            await error.WriteLineAsync($"Error: {ex.Message}").ConfigureAwait(false);
            return 4;
        }
    }
}
