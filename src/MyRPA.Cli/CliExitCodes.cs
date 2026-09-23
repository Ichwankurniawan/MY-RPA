namespace MyRPA.Cli;

/// <summary>Process exit codes returned by the CLI.</summary>
public static class CliExitCodes
{
    /// <summary>The command completed successfully.</summary>
    public const int Success = 0;

    /// <summary>The command failed.</summary>
    public const int Failure = 1;

    /// <summary>The command line was invalid (unknown command, missing command).</summary>
    public const int Usage = 2;

    /// <summary>The command was cancelled (for example Ctrl+C).</summary>
    public const int Cancelled = 130;
}
