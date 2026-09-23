namespace MyRPA.Cli.Commands;

/// <summary>A CLI command. Commands are registered explicitly in <see cref="CliServiceCollectionExtensions"/>.</summary>
public interface ICliCommand
{
    /// <summary>The command word typed by the user, e.g. <c>info</c>.</summary>
    string Name { get; }

    /// <summary>One-line description shown in help.</summary>
    string Description { get; }

    /// <summary>Executes the command.</summary>
    /// <param name="arguments">Arguments after the command word.</param>
    /// <param name="cancellationToken">Cancelled on Ctrl+C / host shutdown.</param>
    /// <returns>A <see cref="CliExitCodes"/> value.</returns>
    Task<int> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}
