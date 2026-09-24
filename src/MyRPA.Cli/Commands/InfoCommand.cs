using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MyRPA.Core.Activities;
using MyRPA.Core.Diagnostics;
using MyRPA.Core.Identifiers;
using MyRPA.Plugins;
using MyRPA.Runtime.Diagnostics;
using MyRPA.Sdk;
using MyRPA.Workflow;

namespace MyRPA.Cli.Commands;

/// <summary>
/// <c>myrpa info</c>: prints host information, including the registered activity types.
/// </summary>
/// <param name="output">CLI output writers.</param>
/// <param name="catalog">Registered activity types.</param>
/// <param name="plugins">Loaded plugins (the source of plugin activities).</param>
/// <param name="scopes">Execution scope factory.</param>
/// <param name="ids">Identifier generator.</param>
/// <param name="logger">Logger.</param>
public sealed partial class InfoCommand(
    CliOutput output,
    IActivityCatalog catalog,
    IPluginRegistry plugins,
    IExecutionScopeFactory scopes,
    IIdGenerator ids,
    ILogger<InfoCommand> logger) : ICliCommand
{
    /// <summary>Span name used by this command.</summary>
    public const string OperationName = "cli.info";

    /// <inheritdoc />
    public string Name => "info";

    /// <inheritdoc />
    public string Usage => "info";

    /// <inheritdoc />
    public string Description => "Show version, runtime and registered activity types.";

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = scopes.Begin(ExecutionIdentity.CreateNew(ids), OperationName);
        LogCollecting(logger);

        var writer = output.Out;
        await writer.WriteLineAsync($"MyRPA {CliVersion.Current}".AsMemory(), cancellationToken).ConfigureAwait(false);
        await WriteFieldAsync(writer, "Runtime", RuntimeInformation.FrameworkDescription, cancellationToken).ConfigureAwait(false);
        await WriteFieldAsync(writer, "OS", RuntimeInformation.OSDescription, cancellationToken).ConfigureAwait(false);
        await WriteFieldAsync(writer, "Workflow schema version", WorkflowSchemaVersion.Current.ToString(), cancellationToken).ConfigureAwait(false);
        await WriteFieldAsync(writer, "Automation SDK", AutomationSdk.Version.ToString(), cancellationToken).ConfigureAwait(false);
        await WriteFieldAsync(writer, "Plugins", plugins.Plugins.Count.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await WriteFieldAsync(writer, "Activity source", DiagnosticNames.RuntimeActivitySource, cancellationToken).ConfigureAwait(false);
        await WriteFieldAsync(writer, "Execution ID", scope.Identity.ExecutionId.ToString(), cancellationToken).ConfigureAwait(false);
        await WriteFieldAsync(writer, "Registered activity types", catalog.Descriptors.Count.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        var sources = plugins.Plugins
            .SelectMany(p => p.Manifest.Activities.Select(a => (Activity: a, Plugin: p.Manifest.Id.Value)))
            .ToDictionary(x => x.Activity, x => x.Plugin);
        foreach (var descriptor in catalog.Descriptors)
        {
            var source = sources.TryGetValue(descriptor.TypeName, out var plugin) ? $" [plugin {plugin}]" : string.Empty;
            await writer.WriteLineAsync($"  {descriptor.TypeName,-22} {descriptor.Description}{source}".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        scope.Complete(Core.Execution.ExecutionStatus.Succeeded);
        return CliExitCodes.Success;
    }

    private static Task WriteFieldAsync(TextWriter writer, string name, string value, CancellationToken cancellationToken) =>
        writer.WriteLineAsync($"{name}: {value}".AsMemory(), cancellationToken);

    [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "Collecting host information")]
    private static partial void LogCollecting(ILogger logger);
}
