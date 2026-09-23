using Microsoft.Extensions.DependencyInjection;
using MyRPA.Activities;
using MyRPA.Cli.Commands;
using MyRPA.Runtime;
using MyRPA.Storage;

namespace MyRPA.Cli;

/// <summary>The CLI composition: the single place where the Phase 1 project graph is wired together.</summary>
public static class CliServiceCollectionExtensions
{
    /// <summary>Registers runtime, activities, storage and CLI services. Logging is configured by the host.</summary>
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

        services.AddSingleton(output);
        services.AddSingleton<CliCommandDispatcher>();
        services.AddSingleton<ICliCommand, InfoCommand>();
        return services;
    }
}
