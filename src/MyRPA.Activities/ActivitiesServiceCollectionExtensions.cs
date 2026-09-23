using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyRPA.Core.Activities;

namespace MyRPA.Activities;

/// <summary>Registers the activity catalog and activity types. Registration is always explicit (ADR-0005).</summary>
public static class ActivitiesServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IActivityCatalog"/>. Phase 1 registers no built-in activity types; Phase 2 adds them here.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddMyRpaActivities(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IActivityCatalog, ActivityCatalog>();
        return services;
    }

    /// <summary>Registers one activity type with the catalog.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="descriptor">The activity type metadata.</param>
    public static IServiceCollection AddActivityDescriptor(this IServiceCollection services, ActivityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);
        services.AddSingleton(descriptor);
        return services;
    }
}
