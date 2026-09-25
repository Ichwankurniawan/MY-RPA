using MyRPA.Core.Activities;
using MyRPA.Workflow.Serialization;

namespace MyRPA.Cli.Commands;

/// <summary>
/// <c>myrpa catalog</c>: prints the metadata of every registered activity (built-in and from <c>--plugin</c>) as an
/// activity catalog snapshot (ADR-0020), for tools that validate or design workflows without loading plugins.
/// </summary>
/// <param name="output">CLI output writers.</param>
/// <param name="catalog">Registered activity types.</param>
public sealed class CatalogCommand(CliOutput output, IActivityCatalog catalog) : ICliCommand
{
    /// <inheritdoc />
    public string Name => "catalog";

    /// <inheritdoc />
    public string Usage => "catalog";

    /// <inheritdoc />
    public string Description => "Print the metadata of all registered activities as JSON.";

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        if (arguments.Count != 0)
        {
            await output.Error.WriteLineAsync($"Usage: myrpa {Usage}").ConfigureAwait(false);
            return CliExitCodes.Usage;
        }

        await output.Out.WriteLineAsync(ActivityCatalogJson.Write(catalog.Descriptors)).ConfigureAwait(false);
        return CliExitCodes.Success;
    }
}
