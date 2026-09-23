namespace MyRPA.Cli;

/// <summary>
/// The CLI's standard output and error writers, injected so commands are testable and so that logs (stderr)
/// never mix with command results (stdout).
/// </summary>
/// <param name="standardOutput">Destination for command results.</param>
/// <param name="standardError">Destination for user-facing error messages.</param>
public sealed class CliOutput(TextWriter standardOutput, TextWriter standardError)
{
    /// <summary>Destination for command results.</summary>
    public TextWriter Out { get; } = standardOutput ?? throw new ArgumentNullException(nameof(standardOutput));

    /// <summary>Destination for user-facing error messages.</summary>
    public TextWriter Error { get; } = standardError ?? throw new ArgumentNullException(nameof(standardError));
}
