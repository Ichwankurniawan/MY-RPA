using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyRPA.Activities;
using MyRPA.Cli.Commands;
using MyRPA.Plugins;
using MyRPA.Runtime;
using MyRPA.Storage;

namespace MyRPA.Cli;

/// <summary>The CLI composition: the single place where the project graph is wired together.</summary>
public static class CliServiceCollectionExtensions
{
    /// <summary>
    /// Registers runtime, activities, storage and CLI services. Logging is configured by the host; plugins are added
    /// afterwards with <c>AddMyRpaPlugins</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="output">CLI output writers.</param>
    public static IServiceCollection AddMyRpaCli(this IServiceCollection services, CliOutput output)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(output);

        services
            .AddMyRpaRuntime()
            .AddMyRpaActivities()
            .AddMyRpaStorage();

        services.TryAddSingleton<IPluginRegistry, NoPlugins>();
        services.AddSingleton(output);
        services.AddSingleton<CliCommandDispatcher>();
        services.AddSingleton<ICliCommand, InfoCommand>();
        services.AddSingleton<ICliCommand, ValidateCommand>();
        services.AddSingleton<ICliCommand, RunCommand>();
        services.AddSingleton<ICliCommand, PluginsCommand>();
        services.AddSingleton<ICliCommand, CatalogCommand>();
        return services;
    }
}
