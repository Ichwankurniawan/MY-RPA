using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyRPA.Core.Diagnostics;
using MyRPA.Core.Identifiers;
using MyRPA.Runtime.Diagnostics;

namespace MyRPA.Runtime.Tests;

public sealed class ExecutionScopeFactoryTests : IDisposable
{
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private readonly ActivityListener _listener;
    private readonly ScopeCapturingLoggerProvider _logs = new();
    private readonly ServiceProvider _services;

    public ExecutionScopeFactoryTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DiagnosticNames.RuntimeActivitySource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(_listener);

        _services = new ServiceCollection()
            .AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(_logs))
            .AddMyRpaRuntime()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public void Dispose()
    {
        _services.Dispose();
        _listener.Dispose();
    }

    [Fact]
    public void Begin_StartsSpanWithIdentityTags_AndStopsOnDispose()
    {
        var identity = ExecutionIdentity.CreateNew().ForWorkflow(new WorkflowId("wf-span")).ForNode(new NodeId("n1"));
        var factory = _services.GetRequiredService<IExecutionScopeFactory>();

        using (var scope = factory.Begin(identity, "test.operation"))
        {
            Assert.NotNull(scope.Activity);
            Assert.Equal("test.operation", scope.Activity.OperationName);
            Assert.Equal(identity.ExecutionId.ToString(), scope.Activity.GetTagItem(DiagnosticNames.ExecutionIdKey));
            Assert.Equal("wf-span", scope.Activity.GetTagItem(DiagnosticNames.WorkflowIdKey));
            Assert.Equal("n1", scope.Activity.GetTagItem(DiagnosticNames.NodeIdKey));
        }

        Assert.Contains(_stopped, a => Equals(a.GetTagItem(DiagnosticNames.ExecutionIdKey), identity.ExecutionId.ToString()));
    }

    [Fact]
    public void Begin_LogsInsideScopeCarryIdentity_AndScopeEndsOnDispose()
    {
        var identity = ExecutionIdentity.CreateNew(new CorrelationId("corr-log"));
        var factory = _services.GetRequiredService<IExecutionScopeFactory>();
        var logger = _services.GetRequiredService<ILogger<ExecutionScopeFactoryTests>>();

        using (factory.Begin(identity, "test.logging"))
        {
            logger.LogInformation("inside");
        }

        logger.LogInformation("outside");

        var inside = Assert.Single(_logs.Entries, e => e.Message == "inside");
        Assert.Equal(identity.ExecutionId.ToString(), inside.ScopeValues[DiagnosticNames.ExecutionIdKey]);
        Assert.Equal("corr-log", inside.ScopeValues[DiagnosticNames.CorrelationIdKey]);

        var outside = Assert.Single(_logs.Entries, e => e.Message == "outside");
        Assert.False(outside.ScopeValues.ContainsKey(DiagnosticNames.ExecutionIdKey));
    }

    [Fact]
    public void Begin_InvalidArguments_Throw()
    {
        var factory = _services.GetRequiredService<IExecutionScopeFactory>();
        Assert.Throws<ArgumentNullException>(() => factory.Begin(null!, "op"));
        Assert.Throws<ArgumentException>(() => factory.Begin(ExecutionIdentity.CreateNew(), " "));
    }
}
