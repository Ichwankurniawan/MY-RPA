using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MyRPA.Core.Activities;
using MyRPA.Core.Diagnostics;
using MyRPA.Runtime.Diagnostics;
using MyRPA.Workflow;

namespace MyRPA.Cli.Commands;

/// <summary>
/// <c>myrpa info</c>: prints host information. Demonstrates the Phase 1 foundation end to end: DI composition,
/// an execution scope (log scope + span) and the explicitly registered activity catalog.
/// </summary>
/// <param name="output">CLI output writers.</param>
/// <param name="catalog">Registered activity types.</param>
/// <param name="scopes">Execution scope factory.</param>
/// <param name="logger">Logger.</param>
public sealed partial class InfoCommand(
    CliOutput output,
    IActivityCatalog catalog,
    IExecutionScopeFactory scopes,
    ILogger<InfoCommand> logger) : ICliCommand
{
    /// <summary>Span name used by this command.</summary>
    public const string OperationName = "cli.info";

    /// <inheritdoc />
    public string Name => "info";

    /// <inheritdoc />
    public string Description => "Show version, runtime and registered components.";

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = scopes.Begin(ExecutionIdentity.CreateNew(), OperationName);
        LogCollecting(logger);

        var writer = output.Out;
        await writer.WriteLineAsync($"MyRPA {CliVersion.Current}".AsMemory(), cancellationToken).ConfigureAwait(false);
        await WriteFieldAsync(writer, "Runtime", RuntimeInformation.FrameworkDescription, cancellationToken).ConfigureAwait(false);
        await WriteFieldAsync(writer, "OS", RuntimeInformation.OSDescription, cancellationToken).ConfigureAwait(false);
        await WriteFieldAsync(writer, "Workflow schema version", WorkflowSchemaVersion.Current.ToString(), cancellationToken).ConfigureAwait(false);
        await WriteFieldAsync(writer, "Activity source", DiagnosticNames.RuntimeActivitySource, cancellationToken).ConfigureAwait(false);
        await WriteFieldAsync(writer, "Execution ID", scope.Identity.ExecutionId.ToString(), cancellationToken).ConfigureAwait(false);
        await WriteFieldAsync(writer, "Registered activity types", catalog.Descriptors.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);

        return CliExitCodes.Success;
    }

    private static Task WriteFieldAsync(TextWriter writer, string name, string value, CancellationToken cancellationToken) =>
        writer.WriteLineAsync($"{name}: {value}".AsMemory(), cancellationToken);

    [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "Collecting host information")]
    private static partial void LogCollecting(ILogger logger);
}
