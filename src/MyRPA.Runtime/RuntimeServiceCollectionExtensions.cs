using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyRPA.Runtime.Diagnostics;

namespace MyRPA.Runtime;

/// <summary>Registers the MyRPA runtime foundation.</summary>
public static class RuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TimeProvider"/>, <see cref="MyRpaTelemetry"/> and <see cref="IExecutionScopeFactory"/>.
    /// Logging itself (<c>AddLogging</c>) is configured by the composition root.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddMyRpaRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<MyRpaTelemetry>();
        services.TryAddSingleton<IExecutionScopeFactory, ExecutionScopeFactory>();
        return services;
    }
}
