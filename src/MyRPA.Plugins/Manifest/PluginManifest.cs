using MyRPA.Core.Activities;
using MyRPA.Sdk;
using MyRPA.Sdk.Automation;
using MyRPA.Sdk.Plugins;

namespace MyRPA.Plugins.Manifest;

/// <summary>
/// A validated plugin manifest (<c>myrpa-plugin.json</c>, ADR-0014). Everything the host needs to decide whether to
/// load a plugin is here, so it can be inspected before any plugin code runs.
/// </summary>
public sealed class PluginManifest
{
    /// <summary>The manifest file name inside a plugin directory.</summary>
    public const string FileName = "myrpa-plugin.json";

    /// <summary>The manifest format version this host reads.</summary>
    public static SdkVersion CurrentManifestVersion { get; } = new(1, 0);

    internal PluginManifest(
        SdkVersion manifestVersion,
        PluginId id,
        string name,
        PluginVersion version,
        string? description,
        SdkVersion sdkVersion,
        string targetFramework,
        PluginEntryPoint entryPoint,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<ActivityTypeName> activities,
        IReadOnlyList<AutomationProviderId> providers,
        IReadOnlyList<PluginDependency> dependencies)
    {
        ManifestVersion = manifestVersion;
        Id = id;
        Name = name;
        Version = version;
        Description = description;
        SdkVersion = sdkVersion;
        TargetFramework = targetFramework;
        EntryPoint = entryPoint;
        Capabilities = capabilities;
        Activities = activities;
        Providers = providers;
        Dependencies = dependencies;
    }

    /// <summary>Manifest format version (<c>manifestVersion</c>).</summary>
    public SdkVersion ManifestVersion { get; }

    /// <summary>Plugin id (<c>id</c>).</summary>
    public PluginId Id { get; }

    /// <summary>Display name (<c>name</c>).</summary>
    public string Name { get; }

    /// <summary>Plugin version (<c>version</c>).</summary>
    public PluginVersion Version { get; }

    /// <summary>Optional description (<c>description</c>).</summary>
    public string? Description { get; }

    /// <summary>The SDK version the plugin was built for (<c>sdkVersion</c>).</summary>
    public SdkVersion SdkVersion { get; }

    /// <summary>Target framework moniker of the plugin assemblies (<c>targetFramework</c>), for example <c>net10.0</c>.</summary>
    public string TargetFramework { get; }

    /// <summary>Entry assembly and type (<c>entryPoint</c>).</summary>
    public PluginEntryPoint EntryPoint { get; }

    /// <summary>Declared capabilities (<c>capabilities</c>; see <see cref="PluginCapabilities"/>).</summary>
    public IReadOnlyList<string> Capabilities { get; }

    /// <summary>Activity type names the plugin registers (<c>activities</c>).</summary>
    public IReadOnlyList<ActivityTypeName> Activities { get; }

    /// <summary>Provider ids the plugin registers (<c>providers</c>).</summary>
    public IReadOnlyList<AutomationProviderId> Providers { get; }

    /// <summary>Plugins this plugin requires (<c>dependencies</c>).</summary>
    public IReadOnlyList<PluginDependency> Dependencies { get; }
}

/// <summary>The assembly file and type that implement <see cref="IPlugin"/>.</summary>
/// <param name="Assembly">File name of the entry assembly in the plugin directory, such as <c>Contoso.Browser.dll</c>.</param>
/// <param name="Type">Full name of the public class implementing <see cref="IPlugin"/>.</param>
public sealed record PluginEntryPoint(string Assembly, string Type);

/// <summary>A dependency on another plugin: same major version, at least <see cref="MinimumVersion"/>.</summary>
/// <param name="Id">The required plugin.</param>
/// <param name="MinimumVersion">The minimum compatible version.</param>
public sealed record PluginDependency(PluginId Id, PluginVersion MinimumVersion);
