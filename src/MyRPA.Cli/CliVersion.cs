using System.Reflection;

namespace MyRPA.Cli;

/// <summary>Version information of the CLI assembly.</summary>
public static class CliVersion
{
    /// <summary>The informational version (e.g. <c>0.1.0</c>, possibly with a source revision suffix).</summary>
    public static string Current { get; } =
        typeof(CliVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";
}
