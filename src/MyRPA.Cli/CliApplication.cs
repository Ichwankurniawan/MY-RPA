using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using MyRPA.Core.Diagnostics;
using MyRPA.Plugins;

namespace MyRPA.Cli;

/// <summary>Builds the Generic Host for one CLI invocation and dispatches the command.</summary>
public static partial class CliApplication
{
    /// <summary>Host application name; equals the executable/assembly name <c>myrpa</c>.</summary>
    public const string ApplicationName = "myrpa";

    private const string VerboseOption = "--verbose";

    private const string PluginOption = "--plugin";

    /// <summary>Runs the CLI.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="standardOutput">Destination for command results.</param>
    /// <param name="standardError">Destination for user-facing errors (logs also go to the process stderr).</param>
    /// <param name="cancellationToken">External cancellation; Ctrl+C is handled by the host lifetime.</param>
    /// <returns>A <see cref="CliExitCodes"/> value.</returns>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var output = new CliOutput(standardOutput, standardError);
        if (!TryParseGlobalOptions(args, out var verbose, out var pluginDirectories, out var commandArgs, out var usageError))
        {
            await output.Error.WriteLineAsync($"Error: {usageError}").ConfigureAwait(false);
            return CliExitCodes.Usage;
        }

        PluginSet? plugins = null;
        try
        {
            if (pluginDirectories.Count > 0)
            {
                plugins = await LoadPluginsAsync(pluginDirectories, cancellationToken).ConfigureAwait(false);
                foreach (var diagnostic in plugins.Diagnostics)
                {
                    await output.Error.WriteLineAsync(diagnostic.ToString()).ConfigureAwait(false);
                }

                if (plugins.HasRequiredFailures)
                {
                    await output.Error.WriteLineAsync("Error: plugins failed to load; nothing was run.").ConfigureAwait(false);
                    return CliExitCodes.PluginFailure;
                }
            }

            return await RunHostAsync(output, verbose, plugins, commandArgs, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await output.Error.WriteLineAsync("Cancelled.").ConfigureAwait(false);
            return CliExitCodes.Cancelled;
        }
        finally
        {
            // After the host (and therefore every plugin service) has been disposed: dispose plugins and unload them.
            if (plugins is not null)
            {
                await plugins.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Builds the host. Exposed for integration tests so they compose exactly what the CLI composes.</summary>
    /// <param name="output">CLI output writers.</param>
    /// <param name="verbose">When <see langword="true"/>, debug logs are written to stderr.</param>
    /// <param name="plugins">Loaded plugins to add; the caller disposes them after the host.</param>
    public static IHost BuildHost(CliOutput output, bool verbose, PluginSet? plugins = null)
    {
        ArgumentNullException.ThrowIfNull(output);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            // Matches the executable/assembly name (Phase 1 review M4).
            ApplicationName = ApplicationName,
            // Configuration files are read from the install directory, never from the caller's working directory.
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning);
        // Messages written by workflows (Core.Log) are user output of `run`; show them at Information and above.
        builder.Logging.AddFilter(DiagnosticNames.WorkflowLogCategory, verbose ? LogLevel.Debug : LogLevel.Information);
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            // Scope values (execution/workflow/node ids) are always available to structured providers; on the console
            // they are shown only with --verbose to keep workflow log output readable.
            options.IncludeScopes = verbose;
        });
        builder.Services.Configure<ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);

        builder.Services.AddMyRpaCli(output);
        if (plugins is not null)
        {
            builder.Services.AddMyRpaPlugins(plugins);
        }

        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        }));

        return builder.Build();
    }

    private static async Task<int> RunHostAsync(CliOutput output, bool verbose, PluginSet? plugins, string[] commandArgs, CancellationToken cancellationToken)
    {
        using var host = BuildHost(output, verbose, plugins);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CliApplication));

        try
        {
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, lifetime.ApplicationStopping);
            var dispatcher = host.Services.GetRequiredService<CliCommandDispatcher>();
            return await dispatcher.DispatchAsync(commandArgs, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await output.Error.WriteLineAsync("Cancelled.").ConfigureAwait(false);
            return CliExitCodes.Cancelled;
        }
#pragma warning disable CA1031 // Top-level boundary: report any failure as an exit code instead of crashing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogUnhandled(logger, ex);
            await output.Error.WriteLineAsync($"Error: {ex.Message}").ConfigureAwait(false);
            return CliExitCodes.Failure;
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static Task<PluginSet> LoadPluginsAsync(IReadOnlyList<string> directories, CancellationToken cancellationToken)
    {
        // Plugins named on the command line are the operator's explicit allow-list; each one is required.
        var options = new PluginHostOptions();
        foreach (var directory in directories)
        {
            options.Sources.Add(new PluginSource { Directory = Path.GetFullPath(directory), Required = true });
        }

        return PluginLoader.LoadAsync(options, cancellationToken);
    }

    private static bool TryParseGlobalOptions(
        string[] args,
        out bool verbose,
        out List<string> pluginDirectories,
        out string[] commandArgs,
        out string? error)
    {
        verbose = false;
        pluginDirectories = [];
        error = null;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], VerboseOption, StringComparison.Ordinal))
            {
                verbose = true;
            }
            else if (string.Equals(args[i], PluginOption, StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    error = "--plugin needs a plugin directory.";
                    commandArgs = [];
                    return false;
                }

                pluginDirectories.Add(args[++i]);
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        commandArgs = [.. rest];
        return true;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Unhandled error while running the CLI")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);
}
