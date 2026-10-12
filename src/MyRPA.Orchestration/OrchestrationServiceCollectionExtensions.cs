using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MyRPA.Orchestration;

/// <summary>Registers the orchestrator.</summary>
public static class OrchestrationServiceCollectionExtensions
{
    /// <summary>
    /// Adds the <see cref="Orchestrator"/> and its id generator. The host adds an <see cref="IOrchestrationStore"/>, a
    /// <see cref="TimeProvider"/> and the workflow loader.
    /// </summary>
    public static IServiceCollection AddMyRpaOrchestration(this IServiceCollection services, Action<OrchestratorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new OrchestratorOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IOrchestrationIds, RandomOrchestrationIds>();
        services.AddSingleton(options);
        services.AddSingleton<Orchestrator>();
        return services;
    }
}
