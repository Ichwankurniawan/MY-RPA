using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyRPA.Workflow.Execution;

namespace MyRPA.Storage;

/// <summary>Registers MyRPA storage services (file-based workflow loading in Phase 2; no database).</summary>
public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="WorkflowFileLoader"/> (singleton) and <see cref="FileWorkflowResolver"/> as the
    /// <see cref="IWorkflowResolver"/> (scoped per run). Requires <c>AddMyRpaRuntime</c> for the loader pipeline.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddMyRpaStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<WorkflowFileLoader>();
        services.TryAddScoped<IWorkflowResolver, FileWorkflowResolver>();
        return services;
    }
}
