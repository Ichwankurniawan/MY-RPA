using Microsoft.Extensions.DependencyInjection;

namespace MyRPA.Storage;

/// <summary>Registers MyRPA storage services.</summary>
/// <remarks>
/// Phase 1 establishes the project boundary and composition entry point only; no storage abstraction is needed
/// yet, and no database is referenced. Phase 2+ adds abstractions here (for example a workflow definition store)
/// and concrete implementations in separate projects.
/// </remarks>
public static class StorageServiceCollectionExtensions
{
    /// <summary>Registers storage services. Currently registers nothing (see remarks).</summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddMyRpaStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
