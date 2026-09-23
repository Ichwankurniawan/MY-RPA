using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MyRPA.Core.Diagnostics;
using MyRPA.Runtime.Diagnostics;

namespace MyRPA.Runtime.Tests;

public sealed class RuntimeCompositionTests
{
    private static ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection().AddLogging();
        configure?.Invoke(services);
        return services.AddMyRpaRuntime()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void AddMyRpaRuntime_RegistersSingletons()
    {
        using var provider = Build();

        Assert.Same(provider.GetRequiredService<IExecutionScopeFactory>(), provider.GetRequiredService<IExecutionScopeFactory>());
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.Equal(DiagnosticNames.RuntimeActivitySource, provider.GetRequiredService<MyRpaTelemetry>().ActivitySource.Name);
    }

    [Fact]
    public void AddMyRpaRuntime_DoesNotOverrideHostProvidedTimeProvider()
    {
        var custom = new FixedTimeProvider();
        using var provider = Build(s => s.AddSingleton<TimeProvider>(custom));

        Assert.Same(custom, provider.GetRequiredService<TimeProvider>());
    }

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

    private sealed class FixedTimeProvider : TimeProvider;
}
