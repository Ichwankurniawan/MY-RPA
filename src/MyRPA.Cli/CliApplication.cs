using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace MyRPA.Cli;

/// <summary>Builds the Generic Host for one CLI invocation and dispatches the command.</summary>
public static partial class CliApplication
{
    private const string VerboseOption = "--verbose";

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
        var verbose = args.Contains(VerboseOption, StringComparer.Ordinal);
        string[] commandArgs = [.. args.Where(a => !string.Equals(a, VerboseOption, StringComparison.Ordinal))];

        using var host = BuildHost(output, verbose);
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

    /// <summary>Builds the host. Exposed for integration tests so they compose exactly what the CLI composes.</summary>
    /// <param name="output">CLI output writers.</param>
    /// <param name="verbose">When <see langword="true"/>, debug logs are written to stderr.</param>
    public static IHost BuildHost(CliOutput output, bool verbose)
    {
        ArgumentNullException.ThrowIfNull(output);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "MyRPA.Cli",
            // Configuration files are read from the install directory, never from the caller's working directory.
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning);
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.IncludeScopes = true;
        });
        builder.Services.Configure<ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);

        builder.Services.AddMyRpaCli(output);

        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        }));

        return builder.Build();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Unhandled error while running the CLI")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);
}
