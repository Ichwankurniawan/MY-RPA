using MyRPA.Storage;

namespace MyRPA.Cli.Commands;

/// <summary>
/// <c>myrpa validate &lt;workflow.json&gt;</c>: runs the full loading pipeline and prints every diagnostic to stdout.
/// Exit codes: 0 valid (warnings allowed), 3 invalid, 2 usage/file problems.
/// </summary>
/// <param name="output">CLI output writers.</param>
/// <param name="files">Workflow file loader.</param>
public sealed class ValidateCommand(CliOutput output, WorkflowFileLoader files) : ICliCommand
{
    /// <inheritdoc />
    public string Name => "validate";

    /// <inheritdoc />
    public string Usage => "validate <workflow.json>";

    /// <inheritdoc />
    public string Description => "Validate a workflow file and report all problems.";

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 1 || arguments[0].StartsWith("--", StringComparison.Ordinal))
        {
            await output.Error.WriteLineAsync($"Usage: myrpa {Usage}").ConfigureAwait(false);
            return CliExitCodes.Usage;
        }

        var resolution = await files.LoadAsync(arguments[0], cancellationToken).ConfigureAwait(false);
        foreach (var diagnostic in resolution.Diagnostics)
        {
            await output.Out.WriteLineAsync(diagnostic.ToString()).ConfigureAwait(false);
        }

        if (resolution.Succeeded)
        {
            var workflow = resolution.Workflow;
            await output.Out.WriteLineAsync(
                $"Valid: {workflow.Id} '{workflow.Name}' version {workflow.Version} (schema {workflow.SchemaVersion})").ConfigureAwait(false);
            return CliExitCodes.Success;
        }

        if (resolution.Diagnostics.Count == 0)
        {
            await output.Error.WriteLineAsync($"Error: {resolution.Error}").ConfigureAwait(false);
            return CliExitCodes.Usage;
        }

        var errors = resolution.Diagnostics.Count(d => d.Severity == Workflow.Validation.DiagnosticSeverity.Error);
        await output.Out.WriteLineAsync($"Invalid: {errors} error(s).").ConfigureAwait(false);
        return CliExitCodes.InvalidWorkflow;
    }
}
