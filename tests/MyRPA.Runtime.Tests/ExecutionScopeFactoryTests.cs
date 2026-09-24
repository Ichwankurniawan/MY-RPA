using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyRPA.Core.Diagnostics;
using MyRPA.Core.Execution;
using MyRPA.Core.Identifiers;
using MyRPA.Runtime.Diagnostics;

namespace MyRPA.Runtime.Tests;

public sealed class ExecutionScopeFactoryTests : IDisposable
{
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private readonly ActivityListener _listener;
    private readonly ScopeCapturingLoggerProvider _logs = new();
    private readonly ServiceProvider _services;
    private readonly SequentialIdGenerator _ids = new();

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
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = true });
    }

    public void Dispose()
    {
        _services.Dispose();
        _listener.Dispose();
    }

    private IExecutionScopeFactory Factory => _services.GetRequiredService<IExecutionScopeFactory>();

    private ExecutionIdentity NewIdentity(string correlation) =>
        ExecutionIdentity.CreateNew(_ids, new CorrelationId(correlation + "-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void Begin_StartsSpanWithIdentityAndExtraTags_AndStopsOnDispose()
    {
        var identity = NewIdentity("span").ForWorkflow(new WorkflowId("wf-span")).ForNode(new NodeId("n1"));

        using (var scope = Factory.Begin(identity, "test.operation", [new("extra", "yes")]))
        {
            Assert.NotNull(scope.Activity);
            Assert.Equal("test.operation", scope.Activity.OperationName);
            Assert.Equal(identity.ExecutionId.ToString(), scope.Activity.GetTagItem(DiagnosticNames.ExecutionIdKey));
            Assert.Equal("wf-span", scope.Activity.GetTagItem(DiagnosticNames.WorkflowIdKey));
            Assert.Equal("n1", scope.Activity.GetTagItem(DiagnosticNames.NodeIdKey));
            Assert.Equal("yes", scope.Activity.GetTagItem("extra"));
        }

        Assert.Contains(_stopped, a => Equals(a.GetTagItem(DiagnosticNames.CorrelationIdKey), identity.CorrelationId.Value));
    }

    [Theory]
    [InlineData(ExecutionStatus.Succeeded, ActivityStatusCode.Ok, false)]
    [InlineData(ExecutionStatus.Failed, ActivityStatusCode.Error, true)]
    [InlineData(ExecutionStatus.TimedOut, ActivityStatusCode.Error, true)]
    [InlineData(ExecutionStatus.Cancelled, ActivityStatusCode.Unset, true)]
    public void Complete_RecordsOutcomeStatusAndException(ExecutionStatus status, ActivityStatusCode expected, bool recordsException)
    {
        using var scope = Factory.Begin(NewIdentity("complete"), "test.complete");

        scope.Complete(status, status == ExecutionStatus.Succeeded ? null : new InvalidOperationException("broken"));

        Assert.Equal(expected, scope.Activity!.Status);
        Assert.Equal(status.ToString(), scope.Activity.GetTagItem(DiagnosticNames.OutcomeKey));
        Assert.Equal(recordsException, scope.Activity.Events.Any(e => e.Name == "exception"));
    }

    [Fact]
    public void Begin_LogsInsideScopeCarryIdentity_AndScopeEndsOnDispose()
    {
        var identity = NewIdentity("log");
        var logger = _services.GetRequiredService<ILogger<ExecutionScopeFactoryTests>>();

        using (Factory.Begin(identity, "test.logging"))
        {
            logger.LogInformation("inside");
        }

        logger.LogInformation("outside");

        var inside = Assert.Single(_logs.Entries, e => e.Message == "inside");
        Assert.Equal(identity.ExecutionId.ToString(), inside.ScopeValues[DiagnosticNames.ExecutionIdKey]);
        Assert.Equal(identity.CorrelationId.Value, inside.ScopeValues[DiagnosticNames.CorrelationIdKey]);

        var outside = Assert.Single(_logs.Entries, e => e.Message == "outside");
        Assert.False(outside.ScopeValues.ContainsKey(DiagnosticNames.ExecutionIdKey));
    }

    [Fact]
    public void ScopeState_ToString_RendersKeyValuePairs()
    {
        // M5: text log providers (the console) render the scope through ToString().
        var identity = new ExecutionIdentity(new ExecutionId(new Guid(7, 0, 0, new byte[8])), new CorrelationId("corr-x"), new WorkflowId("wf"));
        var logger = _services.GetRequiredService<ILogger<ExecutionScopeFactoryTests>>();

        using (Factory.Begin(identity, "test.text"))
        {
            logger.LogInformation("text");
        }

        var entry = Assert.Single(_logs.Entries, e => e.Message == "text");
        Assert.Equal(
            "myrpa.execution.id=00000007000000000000000000000000, myrpa.correlation.id=corr-x, myrpa.workflow.id=wf",
            Assert.Single(entry.ScopeTexts));
    }

    [Fact]
    public void Begin_InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => Factory.Begin(null!, "op"));
        Assert.Throws<ArgumentException>(() => Factory.Begin(NewIdentity("bad"), " "));
    }
}
