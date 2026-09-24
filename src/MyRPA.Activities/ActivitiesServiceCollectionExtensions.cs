using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyRPA.Activities.BuiltIn;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;

namespace MyRPA.Activities;

/// <summary>Registers the activity catalog and activities. Registration is always explicit (ADR-0005).</summary>
public static class ActivitiesServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IActivityCatalog"/>/<see cref="IActivityFactory"/> and the built-in <c>Core.*</c> activities.
    /// Safe to call more than once.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddMyRpaActivities(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ActivityCatalog>();
        services.TryAddSingleton<IActivityCatalog>(sp => sp.GetRequiredService<ActivityCatalog>());
        services.TryAddSingleton<IActivityFactory>(sp => sp.GetRequiredService<ActivityCatalog>());

        if (services.Any(d => d.ServiceType == typeof(BuiltInActivitiesMarker)))
        {
            return services;
        }

        services.AddSingleton<BuiltInActivitiesMarker>();
        return services
            .AddActivity<SequenceActivity>(SequenceActivity.Descriptor)
            .AddActivity<AssignActivity>(AssignActivity.Descriptor)
            .AddActivity<LogActivity>(LogActivity.Descriptor)
            .AddActivity<DelayActivity>(DelayActivity.Descriptor)
            .AddActivity<IfActivity>(IfActivity.Descriptor)
            .AddActivity<SwitchActivity>(SwitchActivity.Descriptor)
            .AddActivity<WhileActivity>(WhileActivity.Descriptor)
            .AddActivity<DoWhileActivity>(DoWhileActivity.Descriptor)
            .AddActivity<ForEachActivity>(ForEachActivity.Descriptor)
            .AddActivity<TryCatchActivity>(TryCatchActivity.Descriptor)
            .AddActivity<ThrowActivity>(ThrowActivity.Descriptor)
            .AddActivity<InvokeWorkflowActivity>(InvokeWorkflowActivity.Descriptor);
    }

    /// <summary>
    /// Registers one activity type. The implementation is not registered as a DI service: the catalog creates one
    /// instance per node invocation and the engine disposes it afterwards (ADR-0013).
    /// </summary>
    /// <typeparam name="TActivity">Implementation type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="descriptor">The activity type metadata.</param>
    public static IServiceCollection AddActivity<TActivity>(this IServiceCollection services, ActivityDescriptor descriptor)
        where TActivity : class, IActivity
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);
        services.AddSingleton(new ActivityRegistration(descriptor, typeof(TActivity)));
        return services;
    }

    private sealed class BuiltInActivitiesMarker;
}
