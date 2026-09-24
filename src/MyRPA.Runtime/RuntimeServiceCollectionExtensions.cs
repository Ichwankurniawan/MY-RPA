using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyRPA.Core.Identifiers;
using MyRPA.Runtime.Diagnostics;
using MyRPA.Runtime.Execution;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Validation;

namespace MyRPA.Runtime;

/// <summary>Registers the MyRPA runtime (engine, loader, identifiers, observability).</summary>
public static class RuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TimeProvider"/>, <see cref="IIdGenerator"/>, <see cref="MyRpaTelemetry"/>,
    /// <see cref="IExecutionScopeFactory"/>, <see cref="WorkflowLoader"/>, <see cref="WorkflowRuntimeOptions"/> and
    /// <see cref="IWorkflowRunner"/>. The composition root must also register logging, an
    /// <see cref="IActivityFactory"/>/<see cref="Core.Activities.IActivityCatalog"/> (e.g. <c>AddMyRpaActivities</c>)
    /// and optionally an <see cref="IWorkflowResolver"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional engine limits configuration.</param>
    public static IServiceCollection AddMyRpaRuntime(this IServiceCollection services, Action<WorkflowRuntimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new WorkflowRuntimeOptions();
        configure?.Invoke(options);
        if (options.MaxInvocationDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configure), "MaxInvocationDepth must not be negative.");
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IIdGenerator, TimeOrderedIdGenerator>();
        services.TryAddSingleton<MyRpaTelemetry>();
        services.TryAddSingleton<IExecutionScopeFactory, ExecutionScopeFactory>();
        services.TryAddSingleton<WorkflowLoader>();
        services.TryAddSingleton(options);
        services.TryAddSingleton<IWorkflowRunner, WorkflowRunner>();
        return services;
    }
}
