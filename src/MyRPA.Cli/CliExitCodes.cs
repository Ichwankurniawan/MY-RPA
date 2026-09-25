namespace MyRPA.Cli;

/// <summary>Process exit codes returned by the CLI.</summary>
public static class CliExitCodes
{
    /// <summary>The command completed successfully (for <c>run</c>: the workflow succeeded).</summary>
    public const int Success = 0;

    /// <summary>The command failed (for <c>run</c>: the workflow failed).</summary>
    public const int Failure = 1;

    /// <summary>The command line was invalid (unknown command, missing/invalid options, file not found).</summary>
    public const int Usage = 2;

    /// <summary>The workflow file failed validation.</summary>
    public const int InvalidWorkflow = 3;

    /// <summary>The workflow timed out.</summary>
    public const int TimedOut = 4;

    /// <summary>A plugin given with <c>--plugin</c> or <c>--plugin-config</c> could not be loaded, or the configuration is invalid (see standard error).</summary>
    public const int PluginFailure = 5;

    /// <summary>The command was cancelled (for example Ctrl+C).</summary>
    public const int Cancelled = 130;
}
