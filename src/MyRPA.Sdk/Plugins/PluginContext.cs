namespace MyRPA.Sdk.Plugins;

/// <summary>What a plugin can see about itself and the host during <see cref="IPlugin.Initialize"/>.</summary>
public sealed class PluginContext
{
    /// <summary>Creates the context (called by the host).</summary>
    /// <param name="plugin">The plugin's identity and location.</param>
    /// <param name="hostSdkVersion">The SDK version implemented by the host.</param>
    /// <param name="settings">Host-supplied settings for this plugin (never secrets; see ADR-0015).</param>
    public PluginContext(PluginInfo plugin, SdkVersion hostSdkVersion, IReadOnlyDictionary<string, string> settings)
    {
        Plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        HostSdkVersion = hostSdkVersion ?? throw new ArgumentNullException(nameof(hostSdkVersion));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>The plugin's identity and location.</summary>
    public PluginInfo Plugin { get; }

    /// <summary>The SDK version implemented by the host (at least the version the manifest requires).</summary>
    public SdkVersion HostSdkVersion { get; }

    /// <summary>Settings configured for this plugin by the host operator.</summary>
    public IReadOnlyDictionary<string, string> Settings { get; }
}

/// <summary>A plugin's identity and location.</summary>
public sealed class PluginInfo
{
    /// <summary>Creates the description (called by the host).</summary>
    /// <param name="id">Plugin id.</param>
    /// <param name="version">Plugin version.</param>
    /// <param name="directory">
    /// Full path of the plugin directory. Plugin assemblies are loaded from their verified path inside it (ADR-0016), so
    /// <c>Assembly.Location</c> also points here; prefer this property to find files shipped with the plugin.
    /// </param>
    public PluginInfo(PluginId id, PluginVersion version, string directory)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Version = version ?? throw new ArgumentNullException(nameof(version));
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
    }

    /// <summary>Plugin id.</summary>
    public PluginId Id { get; }

    /// <summary>Plugin version.</summary>
    public PluginVersion Version { get; }

    /// <summary>Full path of the plugin directory.</summary>
    public string Directory { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Id} {Version}";
}
