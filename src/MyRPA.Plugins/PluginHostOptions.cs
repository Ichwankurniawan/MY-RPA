namespace MyRPA.Plugins;

/// <summary>
/// The host operator's plugin configuration — the allow-list (ADR-0015). Only the directories listed here are ever
/// inspected; nothing is discovered by scanning application or user folders.
/// </summary>
public sealed class PluginHostOptions
{
    /// <summary>The plugins to load, one directory each, in load order (dependencies are ordered automatically).</summary>
    public IList<PluginSource> Sources { get; } = [];

    /// <summary>When <see langword="true"/>, every source must pin <see cref="PluginSource.Sha256"/>.</summary>
    public bool RequireIntegrity { get; set; }

    /// <summary>Capabilities (see <see cref="PluginCapabilities"/>) that plugins may not declare.</summary>
    public ISet<string> DeniedCapabilities { get; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>One configured plugin.</summary>
public sealed class PluginSource
{
    /// <summary>
    /// Full path of the plugin directory: it directly contains <c>myrpa-plugin.json</c> and the plugin's files. Relative
    /// paths are resolved by the composition root before loading.
    /// </summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>
    /// Optional pinned SHA-256 digest (64 hex characters) of the whole plugin directory, as shown by <c>myrpa plugins</c>.
    /// When set, the plugin loads only if its content matches, and every assembly is re-verified as it is loaded.
    /// </summary>
    public string? Sha256 { get; set; }

    /// <summary>
    /// When <see langword="true"/> (the default), a failure to load this plugin fails host startup. When
    /// <see langword="false"/>, the failure is reported as a diagnostic and the host continues without the plugin.
    /// </summary>
    public bool Required { get; set; } = true;

    /// <summary>Settings passed to the plugin in <c>PluginContext.Settings</c>. Never put secrets here.</summary>
    public IDictionary<string, string> Settings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
