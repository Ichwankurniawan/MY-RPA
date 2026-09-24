namespace MyRPA.Sdk.Plugins;

/// <summary>
/// The entry point of a plugin (ADR-0014). The host instantiates the type named by the manifest's <c>entryPoint</c>
/// (a public, non-abstract class with a public parameterless constructor) and drives the lifecycle:
/// <c>Discover → Validate manifest → Load → Initialize → Register → Use → Dispose</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Identity, version, SDK version, capabilities, activities and providers are declared in the manifest
/// (<c>myrpa-plugin.json</c>), not in code, so they can be inspected and validated before any plugin code runs.</item>
/// <item><see cref="Initialize"/> and <see cref="Register"/> run once, at host composition time, before any workflow
/// runs. They must be fast and must not start background work, open network connections or show UI.</item>
/// <item>If either throws, the plugin is rejected with a diagnostic, none of its registrations are applied, and it is
/// unloaded. The host keeps running unless the plugin was configured as required.</item>
/// <item>If the entry type implements <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>, it is disposed when
/// the host shuts down, after every service it registered has been disposed.</item>
/// </list>
/// </remarks>
public interface IPlugin
{
    /// <summary>Validates configuration and prepares the plugin. Throw to reject the plugin.</summary>
    /// <param name="context">The plugin's identity, directory, settings and the host SDK version.</param>
    void Initialize(PluginContext context);

    /// <summary>
    /// Registers exactly the activities and providers declared in the manifest, plus any services they need. Nothing is
    /// applied to the host until this method returns and every registration has been validated.
    /// </summary>
    /// <param name="registrar">The registration API.</param>
    void Register(IPluginRegistrar registrar);
}
