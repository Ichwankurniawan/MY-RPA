using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using MyRPA.Plugins.Loading;
using MyRPA.Plugins.Manifest;
using MyRPA.Sdk.Plugins;
using MyRPA.Workflow.Validation;

namespace MyRPA.Plugins;

/// <summary>Read-only view of the loaded plugins, for host features such as <c>myrpa plugins</c> and Studio.</summary>
public interface IPluginRegistry
{
    /// <summary>The plugins that loaded successfully, in load order.</summary>
    IReadOnlyList<LoadedPlugin> Plugins { get; }

    /// <summary>Every diagnostic produced while loading (including those of rejected plugins).</summary>
    IReadOnlyList<PluginDiagnostic> Diagnostics { get; }
}

/// <summary>A plugin that passed every lifecycle step up to registration.</summary>
public sealed class LoadedPlugin
{
    internal LoadedPlugin(PluginManifest manifest, string directory, string digest, PluginLoadContext context, IPlugin instance, PluginRegistrar registrar)
    {
        Manifest = manifest;
        Directory = directory;
        Digest = digest;
        Context = context;
        Instance = instance;
        Registrar = registrar;
    }

    /// <summary>The validated manifest.</summary>
    public PluginManifest Manifest { get; }

    /// <summary>Full path of the plugin directory.</summary>
    public string Directory { get; }

    /// <summary>SHA-256 digest of the plugin directory (the value to pin in <see cref="PluginSource.Sha256"/>).</summary>
    public string Digest { get; }

    internal PluginLoadContext Context { get; }

    internal IPlugin Instance { get; }

    internal PluginRegistrar Registrar { get; }
}

/// <summary>
/// The outcome of <see cref="PluginLoader.LoadAsync"/>: the loaded plugins, their staged registrations and all
/// diagnostics. Owns the plugins' load contexts: dispose it <em>after</em> the service provider it was added to, so
/// plugin services are disposed before plugin entry objects are disposed and their contexts are unloaded.
/// </summary>
public sealed class PluginSet : IPluginRegistry, IAsyncDisposable
{
    private readonly List<LoadedPlugin> _plugins;
    private readonly List<PluginDiagnostic> _diagnostics;
    private bool _applied;
    private bool _disposed;

    internal PluginSet(List<LoadedPlugin> plugins, List<PluginDiagnostic> diagnostics, bool hasRequiredFailures)
    {
        _plugins = plugins;
        _diagnostics = diagnostics;
        HasRequiredFailures = hasRequiredFailures;
    }

    /// <inheritdoc />
    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    /// <inheritdoc />
    public IReadOnlyList<PluginDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Whether a plugin configured as required was rejected (the host should not start).</summary>
    public bool HasRequiredFailures { get; }

    /// <summary>
    /// Applies the loaded plugins' registrations to the host's services and registers <see cref="IPluginRegistry"/>.
    /// Call once, after the host's own registrations (including the activity catalog).
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    /// <exception cref="InvalidOperationException">Already applied, disposed, or a plugin activity name is taken.</exception>
    public void AddTo(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_applied)
        {
            throw new InvalidOperationException("The plugin set has already been applied to a service collection.");
        }

        var hostActivities = services.Select(d => d.ImplementationInstance).OfType<MyRPA.Activities.ActivityRegistration>()
            .Select(r => r.Descriptor.TypeName).ToHashSet();
        var taken = _plugins.SelectMany(p => p.Registrar.Activities.Select(a => a.Descriptor.TypeName)).Where(hostActivities.Contains).ToList();
        if (taken.Count > 0)
        {
            throw new InvalidOperationException($"Plugin activities conflict with host activities: {string.Join(", ", taken)}.");
        }

        foreach (var plugin in _plugins)
        {
            plugin.Registrar.Apply(services);
        }

        services.AddSingleton<IPluginRegistry>(this);
        _applied = true;
    }

    /// <summary>
    /// Creates every plugin provider once, at host startup, so a provider whose <c>Descriptor.Id</c> does not match its
    /// registration (or whose constructor fails) is reported before any workflow runs rather than at first use.
    /// </summary>
    /// <param name="services">The host's service provider built from the collection passed to <see cref="AddTo"/>.</param>
    /// <exception cref="InvalidOperationException">A provider could not be created or reports the wrong id.</exception>
    public void VerifyProviders(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var plugin in _plugins)
        {
            foreach (var serviceType in plugin.Registrar.ProviderServiceTypes)
            {
                try
                {
                    _ = services.GetRequiredService(serviceType);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    throw new InvalidOperationException($"Plugin '{plugin.Manifest.Id}': provider {serviceType.Name} could not be created: {ex.Message}", ex);
                }
            }
        }
    }

    /// <summary>
    /// Disposes every plugin entry object (in reverse load order) and unloads the plugin contexts. Unloading completes
    /// when nothing references plugin code any more; a plugin that keeps threads or static roots alive cannot be
    /// unloaded (ADR-0014). Disposal failures are added to <see cref="Diagnostics"/> as warnings, never thrown.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var i = _plugins.Count - 1; i >= 0; i--)
        {
            var plugin = _plugins[i];
            if (await PluginLoader.DisposeQuietlyAsync(plugin.Instance).ConfigureAwait(false) is { } error)
            {
                _diagnostics.Add(new PluginDiagnostic(
                    PluginDiagnosticCodes.DisposeFailed, DiagnosticSeverity.Warning, $"Dispose failed: {error.GetType().Name}: {error.Message}", plugin.Directory, plugin.Manifest.Id.Value));
            }

            plugin.Context.Unload();
        }
    }

    /// <summary>Weak references to the plugin load contexts, for verifying that unloading completed.</summary>
    internal IReadOnlyList<WeakReference> ContextReferences() => [.. _plugins.Select(p => new WeakReference((AssemblyLoadContext)p.Context))];
}
