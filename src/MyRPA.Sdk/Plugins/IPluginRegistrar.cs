using MyRPA.Core.Activities;
using MyRPA.Sdk.Automation;
using MyRPA.Workflow.Execution;

namespace MyRPA.Sdk.Plugins;

/// <summary>
/// The only way a plugin contributes to the host (ADR-0014). Registrations are staged, validated as a whole when
/// <see cref="IPlugin.Register"/> returns, and applied to the host only if all of them are valid:
/// <list type="bullet">
/// <item>the registered activity type names and provider ids must equal those declared in the manifest;</item>
/// <item>service and implementation types must be defined by the plugin itself — a plugin can add services, but
/// never replace or decorate host services;</item>
/// <item>every lifetime is explicit (<see cref="PluginServiceLifetime"/>); there is no transient lifetime.</item>
/// </list>
/// </summary>
public interface IPluginRegistrar
{
    /// <summary>The registering plugin.</summary>
    PluginInfo Plugin { get; }

    /// <summary>
    /// Registers an activity type. One <typeparamref name="TActivity"/> instance is created per node invocation from
    /// its single public constructor (parameters are services) and disposed right after it (ADR-0013).
    /// </summary>
    /// <typeparam name="TActivity">The implementation, defined by this plugin.</typeparam>
    /// <param name="descriptor">Metadata; its type name must be declared in the manifest's <c>activities</c>.</param>
    IPluginRegistrar AddActivity<TActivity>(ActivityDescriptor descriptor)
        where TActivity : class, IActivity;

    /// <summary>
    /// Registers an automation provider with plugin lifetime, exposed to activities as <typeparamref name="TService"/>
    /// (for example a technology interface such as <c>IBrowserProvider</c>). The provider's
    /// <see cref="IAutomationProvider.Descriptor"/> id must equal <paramref name="providerId"/>.
    /// </summary>
    /// <typeparam name="TService">The interface activities depend on, defined by this plugin.</typeparam>
    /// <typeparam name="TProvider">The implementation, defined by this plugin.</typeparam>
    /// <param name="providerId">The provider id; must be declared in the manifest's <c>providers</c>.</param>
    IPluginRegistrar AddProvider<TService, TProvider>(AutomationProviderId providerId)
        where TService : class, IAutomationProvider
        where TProvider : class, TService;

    /// <summary>Registers a plugin-defined service with an explicit lifetime.</summary>
    /// <typeparam name="TService">The service type, defined by this plugin.</typeparam>
    /// <typeparam name="TImplementation">The implementation, defined by this plugin.</typeparam>
    /// <param name="lifetime">Who owns instances and when they are disposed.</param>
    IPluginRegistrar AddService<TService, TImplementation>(PluginServiceLifetime lifetime)
        where TService : class
        where TImplementation : class, TService;

    /// <summary>
    /// Registers an existing instance (for example options built from <see cref="PluginContext.Settings"/>). The plugin
    /// owns the instance: the host never disposes it.
    /// </summary>
    /// <typeparam name="TService">The service type, defined by this plugin.</typeparam>
    /// <param name="instance">The instance.</param>
    IPluginRegistrar AddInstance<TService>(TService instance)
        where TService : class;
}

/// <summary>Explicit ownership of plugin services (ADR-0013).</summary>
public enum PluginServiceLifetime
{
    /// <summary>
    /// One instance for the plugin's lifetime, shared by every run and every thread (must be thread-safe); disposed when
    /// the host shuts down. Use for providers, clients and pools.
    /// </summary>
    Plugin = 0,

    /// <summary>
    /// One instance per top-level workflow run, shared with the workflows it invokes; disposed when the run ends —
    /// whether it succeeded, failed, timed out or was cancelled. Use for sessions and caches that belong to a run.
    /// </summary>
    Run = 1,
}
