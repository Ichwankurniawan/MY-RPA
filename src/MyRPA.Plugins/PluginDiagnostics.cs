using MyRPA.Workflow.Validation;

namespace MyRPA.Plugins;

/// <summary>A problem found while discovering, validating, loading or registering a plugin.</summary>
/// <param name="Code">Stable code (see <see cref="PluginDiagnosticCodes"/>).</param>
/// <param name="Severity">Error (the plugin is rejected) or warning.</param>
/// <param name="Message">Human-readable description.</param>
/// <param name="Source">The configured plugin directory the problem belongs to.</param>
/// <param name="PluginId">The plugin id, when the manifest was read far enough to know it.</param>
/// <param name="Path">JSON path inside the manifest, when applicable.</param>
public sealed record PluginDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string Source,
    string? PluginId = null,
    string? Path = null)
{
    /// <inheritdoc />
    public override string ToString()
    {
        var severity = Severity == DiagnosticSeverity.Error ? "error" : "warning";
        var subject = PluginId is null ? $"plugin at '{Source}'" : $"plugin '{PluginId}'";
        var path = Path is null ? string.Empty : $" {Path}";
        return $"{severity} {Code} {subject}{path}: {Message}";
    }
}

/// <summary>Stable plugin diagnostic codes (MYRPA3xxx).</summary>
public static class PluginDiagnosticCodes
{
    /// <summary>The plugin directory is missing, is a link, contains links, or is too large.</summary>
    public const string DirectoryInvalid = "MYRPA3001";

    /// <summary>The directory has no <c>myrpa-plugin.json</c>.</summary>
    public const string ManifestMissing = "MYRPA3002";

    /// <summary>The manifest is not valid JSON or is too large.</summary>
    public const string ManifestMalformed = "MYRPA3003";

    /// <summary>A required manifest field is missing.</summary>
    public const string MissingField = "MYRPA3004";

    /// <summary>A manifest field has the wrong type or format.</summary>
    public const string InvalidField = "MYRPA3005";

    /// <summary>The manifest has a field this host does not know (warning).</summary>
    public const string UnknownField = "MYRPA3006";

    /// <summary>The manifest format version is not supported.</summary>
    public const string UnsupportedManifestVersion = "MYRPA3007";

    /// <summary>The plugin requires an SDK version this host does not implement.</summary>
    public const string IncompatibleSdk = "MYRPA3008";

    /// <summary>The plugin's target framework cannot run on this host.</summary>
    public const string IncompatibleFramework = "MYRPA3009";

    /// <summary>Another configured plugin has the same id.</summary>
    public const string DuplicatePlugin = "MYRPA3010";

    /// <summary>A declared dependency is not configured.</summary>
    public const string DependencyMissing = "MYRPA3011";

    /// <summary>A declared dependency has an incompatible version.</summary>
    public const string DependencyVersion = "MYRPA3012";

    /// <summary>Plugins depend on each other in a cycle.</summary>
    public const string DependencyCycle = "MYRPA3013";

    /// <summary>A dependency was rejected, so its dependents are too.</summary>
    public const string DependencyFailed = "MYRPA3014";

    /// <summary>The directory digest does not match the pinned SHA-256.</summary>
    public const string IntegrityMismatch = "MYRPA3015";

    /// <summary>The host requires a pinned digest and none is configured.</summary>
    public const string IntegrityPinRequired = "MYRPA3016";

    /// <summary>The plugin declares a capability the host denies.</summary>
    public const string CapabilityDenied = "MYRPA3017";

    /// <summary>The entry point assembly or type is missing or unusable.</summary>
    public const string EntryPointInvalid = "MYRPA3018";

    /// <summary>Loading the plugin's assemblies failed.</summary>
    public const string LoadFailed = "MYRPA3019";

    /// <summary><c>IPlugin.Initialize</c> threw.</summary>
    public const string InitializationFailed = "MYRPA3020";

    /// <summary><c>IPlugin.Register</c> threw or its registrations were invalid.</summary>
    public const string RegistrationFailed = "MYRPA3021";

    /// <summary>An activity or provider name is reserved or declared by another plugin.</summary>
    public const string NameConflict = "MYRPA3022";

    /// <summary>Disposing the plugin failed (warning; reported at shutdown).</summary>
    public const string DisposeFailed = "MYRPA3023";
}

/// <summary>
/// Capabilities a plugin can declare in its manifest: the kinds of host resources it may use. Declarations are
/// informational and policy input (<see cref="PluginHostOptions.DeniedCapabilities"/>); they are not enforced at run
/// time, because in-process plugins are fully trusted code (ADR-0015).
/// </summary>
public static class PluginCapabilities
{
    /// <summary>Reads or writes files.</summary>
    public const string FileSystem = "FileSystem";

    /// <summary>Opens network connections.</summary>
    public const string Network = "Network";

    /// <summary>Starts processes.</summary>
    public const string Process = "Process";

    /// <summary>Drives the interactive desktop (input, windows, screen).</summary>
    public const string Desktop = "Desktop";

    /// <summary>Reads or writes the clipboard.</summary>
    public const string Clipboard = "Clipboard";

    /// <summary>Uses credentials or secrets.</summary>
    public const string Credentials = "Credentials";

    /// <summary>Loads native code.</summary>
    public const string NativeCode = "NativeCode";

    /// <summary>All known capabilities.</summary>
    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        FileSystem, Network, Process, Desktop, Clipboard, Credentials, NativeCode,
    };
}
