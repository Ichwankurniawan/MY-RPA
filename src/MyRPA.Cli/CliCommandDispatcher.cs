using MyRPA.Cli.Commands;

namespace MyRPA.Cli;

/// <summary>
/// Maps the first command-line argument to a registered <see cref="ICliCommand"/> and handles
/// <c>help</c>/<c>--help</c>/<c>-h</c> and <c>version</c>/<c>--version</c>.
/// </summary>
/// <remarks>Deliberately minimal; Phase 2 may replace it with System.CommandLine (ADR-0005).</remarks>
public sealed class CliCommandDispatcher
{
    private readonly IReadOnlyDictionary<string, ICliCommand> _commands;
    private readonly CliOutput _output;

    /// <summary>Creates the dispatcher.</summary>
    /// <param name="commands">Explicitly registered commands.</param>
    /// <param name="output">CLI output writers.</param>
    /// <exception cref="InvalidOperationException">Two commands share a name.</exception>
    public CliCommandDispatcher(IEnumerable<ICliCommand> commands, CliOutput output)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _output = output ?? throw new ArgumentNullException(nameof(output));

        var byName = new SortedDictionary<string, ICliCommand>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands)
        {
            if (!byName.TryAdd(command.Name, command))
            {
                throw new InvalidOperationException($"CLI command '{command.Name}' is registered more than once.");
            }
        }

        _commands = byName;
    }

    /// <summary>Dispatches <paramref name="arguments"/> (global options already removed).</summary>
    /// <param name="arguments">Command word followed by its arguments.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A <see cref="CliExitCodes"/> value.</returns>
    public async Task<int> DispatchAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count == 0)
        {
            await WriteHelpAsync(_output.Error).ConfigureAwait(false);
            return CliExitCodes.Usage;
        }

        var name = arguments[0];
        switch (name)
        {
            case "help" or "--help" or "-h":
                await WriteHelpAsync(_output.Out).ConfigureAwait(false);
                return CliExitCodes.Success;
            case "version" or "--version":
                await _output.Out.WriteLineAsync($"MyRPA {CliVersion.Current}").ConfigureAwait(false);
                return CliExitCodes.Success;
        }

        if (!_commands.TryGetValue(name, out var command))
        {
            await _output.Error.WriteLineAsync($"Unknown command '{name}'.").ConfigureAwait(false);
            await WriteHelpAsync(_output.Error).ConfigureAwait(false);
            return CliExitCodes.Usage;
        }

        return await command.ExecuteAsync([.. arguments.Skip(1)], cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteHelpAsync(TextWriter writer)
    {
        await writer.WriteLineAsync("Usage: myrpa [--verbose] [--plugin <directory>]... [--plugin-config <file>] <command> [arguments]").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("Commands:").ConfigureAwait(false);
        foreach (var command in _commands.Values)
        {
            await writer.WriteLineAsync($"  {command.Name,-10} {command.Description}").ConfigureAwait(false);
            if (!string.Equals(command.Usage, command.Name, StringComparison.Ordinal))
            {
                await writer.WriteLineAsync($"  {string.Empty,-10} myrpa {command.Usage}").ConfigureAwait(false);
            }
        }

        await writer.WriteLineAsync($"  {"help",-10} Show this help.").ConfigureAwait(false);
        await writer.WriteLineAsync($"  {"version",-10} Show the version.").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("Options:").ConfigureAwait(false);
        await writer.WriteLineAsync("  --verbose           Write debug logs to standard error (workflow Log messages are always shown).").ConfigureAwait(false);
        await writer.WriteLineAsync("  --plugin <directory> Load the plugin in <directory> (contains myrpa-plugin.json). Repeatable.").ConfigureAwait(false);
        await writer.WriteLineAsync("  --plugin-config <file> Load the plugins listed in a plugin configuration file (pins, settings).").ConfigureAwait(false);
        await writer.WriteLineAsync("                      Plugins run with full trust: only load plugins you trust.").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("Exit codes: 0 success, 1 failure, 2 usage, 3 invalid workflow, 4 timed out, 5 plugin failure, 130 cancelled.").ConfigureAwait(false);
    }
}
