using Microsoft.Extensions.DependencyInjection;

namespace MyRPA.Plugins;

/// <summary>Adds loaded plugins to a host. Plugins are always configured explicitly (ADR-0005, ADR-0015).</summary>
public static class PluginsServiceCollectionExtensions
{
    /// <summary>
    /// Applies <paramref name="plugins"/> (from <see cref="PluginLoader.LoadAsync"/>) to the host's services. The caller
    /// keeps ownership of <paramref name="plugins"/> and disposes it after the service provider.
    /// </summary>
    /// <param name="services">The host's service collection (activities must already be added).</param>
    /// <param name="plugins">The loaded plugins.</param>
    public static IServiceCollection AddMyRpaPlugins(this IServiceCollection services, PluginSet plugins)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(plugins);
        plugins.AddTo(services);
        return services;
    }
}
