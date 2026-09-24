using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MyRPA.Core.Activities;
using MyRPA.Core.Diagnostics;
using MyRPA.Core.Identifiers;
using MyRPA.Runtime.Diagnostics;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Validation;

namespace MyRPA.Runtime.Tests;

public sealed class RuntimeCompositionTests
{
    private static ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var activities = new TestActivities();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IActivityCatalog>(activities)
            .AddSingleton<IActivityFactory>(activities);
        configure?.Invoke(services);
        return services.AddMyRpaRuntime()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void AddMyRpaRuntime_RegistersEngineAsSingletons()
    {
        using var provider = Build();

        Assert.Same(provider.GetRequiredService<IWorkflowRunner>(), provider.GetRequiredService<IWorkflowRunner>());
        Assert.Same(provider.GetRequiredService<IExecutionScopeFactory>(), provider.GetRequiredService<IExecutionScopeFactory>());
        Assert.NotNull(provider.GetRequiredService<WorkflowLoader>());
        Assert.IsType<TimeOrderedIdGenerator>(provider.GetRequiredService<IIdGenerator>());
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.Equal(DiagnosticNames.RuntimeActivitySource, provider.GetRequiredService<MyRpaTelemetry>().ActivitySource.Name);
        Assert.Equal(10, provider.GetRequiredService<WorkflowRuntimeOptions>().MaxInvocationDepth);
    }

    [Fact]
    public void AddMyRpaRuntime_DoesNotOverrideHostProvidedServices()
    {
        var time = new FakeTimeProvider();
        var ids = new SequentialIdGenerator();
        using var provider = Build(s => s.AddSingleton<TimeProvider>(time).AddSingleton<IIdGenerator>(ids));

        Assert.Same(time, provider.GetRequiredService<TimeProvider>());
        Assert.Same(ids, provider.GetRequiredService<IIdGenerator>());
    }

    [Fact]
    public void AddMyRpaRuntime_RejectsNegativeDepth() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddMyRpaRuntime(o => o.MaxInvocationDepth = -1));

    [Fact]
    public void Telemetry_IsDisposedWithContainer()
    {
        ActivitySource source;
        using (var provider = Build())
        {
            source = provider.GetRequiredService<MyRpaTelemetry>().ActivitySource;
        }

        using var listener = new ActivityListener
        {
            ShouldListenTo = s => ReferenceEquals(s, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        Assert.Null(source.StartActivity("after-dispose"));
    }
}
