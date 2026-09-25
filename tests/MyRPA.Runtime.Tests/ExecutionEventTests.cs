using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MyRPA.Core.Activities;
using MyRPA.Core.Execution;
using MyRPA.Core.Identifiers;
using MyRPA.Workflow.Execution;

namespace MyRPA.Runtime.Tests;

/// <summary>The per-run execution-event hook (ADR-0023).</summary>
public sealed class ExecutionEventTests
{
    private const string Variables = """[ { "name": "x", "type": "Int", "default": 0 } ]""";

    private static readonly string _twoSteps = RuntimeHarness.Workflow(
        """
        { "id": "main", "type": "Test.Sequence", "children": [
          { "id": "a", "type": "Test.Set", "properties": { "to": "x", "value": "x + 1" } },
          { "id": "b", "type": "Test.Set", "properties": { "to": "x", "value": "x + 1" } } ] }
        """,
        variables: Variables);

    [Fact]
    public async Task Run_EmitsStartedNodeAndCompletedEvents_InExecutionOrder()
    {
        using var h = new RuntimeHarness();
        var observer = new RecordingObserver();

        var result = await h.Runner.RunTestAsync(h.Load(_twoSteps), new WorkflowRunRequest { Observer = observer });

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(
            ["ExecutionStarted", "NodeStarted main", "NodeStarted a", "NodeCompleted a Succeeded", "NodeStarted b", "NodeCompleted b Succeeded", "NodeCompleted main Succeeded", "ExecutionCompleted Succeeded"],
            observer.Describe());
        Assert.All(observer.Events, e =>
        {
            Assert.Equal(result.ExecutionId, e.ExecutionId);
            Assert.Equal(result.CorrelationId, e.CorrelationId);
            Assert.Null(e.ParentExecutionId);
            Assert.Equal(RuntimeHarness.Start, e.Time);
        });
        var completed = Assert.IsType<ExecutionCompleted>(observer.Events.Last());
        Assert.Equal(result.Duration, completed.Duration);
        Assert.Equal(result.WorkflowId, completed.WorkflowId);
        Assert.Equal(new ActivityTypeName("Test.Set"), Assert.IsType<NodeStarted>(observer.Events.ElementAt(2)).ActivityType);
    }

    [Fact]
    public async Task Failure_ReportsTheFailedNode_AndItsAncestors_WithTheOriginatingNode()
    {
        using var h = new RuntimeHarness();
        var observer = new RecordingObserver();
        var workflow = h.Load(RuntimeHarness.Workflow(
            """
            { "id": "main", "type": "Test.Sequence", "children": [ { "id": "boom", "type": "Test.Fail", "properties": { "message": "'broken'" } },
              { "id": "never", "type": "Test.Set", "properties": { "to": "x", "value": "1" } } ] }
            """,
            variables: Variables));

        var result = await h.Runner.RunTestAsync(workflow, new WorkflowRunRequest { Observer = observer });

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(["ExecutionStarted", "NodeStarted main", "NodeStarted boom", "NodeCompleted boom Failed", "NodeCompleted main Failed", "ExecutionCompleted Failed"], observer.Describe());
        var nodeFailures = observer.Events.OfType<NodeCompleted>().ToList();
        Assert.All(nodeFailures, n => Assert.Equal("boom", n.Error!.NodeId));
        Assert.Equal(result.Error, Assert.IsType<ExecutionCompleted>(observer.Events.Last()).Error);
    }

    [Fact]
    public async Task Cancellation_ReportsCancelledNodes_AndACancelledExecution()
    {
        using var h = new RuntimeHarness();
        var observer = new RecordingObserver();
        var workflow = h.Load(RuntimeHarness.Workflow(
            """{ "id": "main", "type": "Test.Sequence", "children": [ { "id": "wait", "type": "Test.Wait", "properties": { "milliseconds": 60000 } } ] }"""));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = h.Runner.RunAsync(workflow, new WorkflowRunRequest { Observer = observer }, cancellation.Token);
        await observer.WaitForAsync("NodeStarted wait");
        await cancellation.CancelAsync();
        var result = await run;

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Equal(["ExecutionStarted", "NodeStarted main", "NodeStarted wait", "NodeCompleted wait Cancelled", "NodeCompleted main Cancelled", "ExecutionCompleted Cancelled"], observer.Describe());
        Assert.All(observer.Events.OfType<NodeCompleted>(), n => Assert.Null(n.Error));
    }

    [Fact]
    public async Task Timeout_ReportsCancelledNodes_AndATimedOutExecution()
    {
        using var h = new RuntimeHarness();
        var observer = new RecordingObserver();
        var workflow = h.Load(RuntimeHarness.Workflow("""{ "id": "wait", "type": "Test.Wait", "properties": { "milliseconds": 60000 } }"""));

        var run = h.Runner.RunTestAsync(workflow, new WorkflowRunRequest { Observer = observer, Timeout = TimeSpan.FromSeconds(1) });
        await observer.WaitForAsync("NodeStarted wait");
        h.Time.Advance(TimeSpan.FromSeconds(2));
        var result = await run;

        Assert.Equal(ExecutionStatus.TimedOut, result.Status);
        Assert.Equal(["ExecutionStarted", "NodeStarted wait", "NodeCompleted wait Cancelled", "ExecutionCompleted TimedOut"], observer.Describe());
        Assert.Equal(ExecutionErrorCodes.TimedOut, Assert.IsType<ExecutionCompleted>(observer.Events.Last()).Error!.Code);
    }

    [Fact]
    public async Task RejectedArguments_EmitOnlyStartedAndCompleted()
    {
        using var h = new RuntimeHarness();
        var observer = new RecordingObserver();
        var workflow = h.Load(RuntimeHarness.Workflow(
            """{ "id": "main", "type": "Test.Sequence" }""", """[ { "name": "n", "direction": "In", "type": "Int", "required": true } ]"""));

        var result = await h.Runner.RunTestAsync(workflow, new WorkflowRunRequest { Observer = observer });

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(["ExecutionStarted", "ExecutionCompleted Failed"], observer.Describe());
        Assert.Equal(ExecutionErrorCodes.InvalidArguments, Assert.IsType<ExecutionCompleted>(observer.Events.Last()).Error!.Code);
    }

    [Fact]
    public async Task Invocation_ChildEventsCarryTheChildExecution_NestedInsideTheInvokingNode()
    {
        using var h = new RuntimeHarness(new Dictionary<string, string>
        {
            ["child"] = RuntimeHarness.Workflow("""{ "id": "inner", "type": "Test.Set", "properties": { "to": "x", "value": "1" } }""", variables: Variables, id: "child"),
        });
        var observer = new RecordingObserver();
        var parent = h.Load(RuntimeHarness.Workflow("""{ "id": "call", "type": "Test.Invoke", "properties": { "workflow": "child" } }""", id: "parent"));

        var result = await h.Runner.RunTestAsync(parent, new WorkflowRunRequest { Location = "parent", Observer = observer });

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(
            ["ExecutionStarted", "NodeStarted call", "ExecutionStarted", "NodeStarted inner", "NodeCompleted inner Succeeded", "ExecutionCompleted Succeeded", "NodeCompleted call Succeeded", "ExecutionCompleted Succeeded"],
            observer.Describe());
        var events = observer.Events.ToList();
        var child = events.Skip(2).Take(4).ToList();
        Assert.All(child, e =>
        {
            Assert.NotEqual(result.ExecutionId, e.ExecutionId);
            Assert.Equal(result.ExecutionId, e.ParentExecutionId);
            Assert.Equal(result.CorrelationId, e.CorrelationId);
        });
        Assert.Equal(new WorkflowId("child"), Assert.IsType<ExecutionStarted>(child[0]).WorkflowId);
        Assert.All(events.Except(child), e => Assert.Equal(result.ExecutionId, e.ExecutionId));
    }

    [Theory]
    [InlineData(0)] // the very first event
    [InlineData(4)] // mid-run (NodeStarted b)
    public async Task ThrowingObserver_DoesNotChangeTheOutcome_AndIsNotCalledAgain(int failOnCall)
    {
        using var h = new RuntimeHarness();
        var observer = new ThrowingObserver(failOnCall, new InvalidOperationException("observer bug"));
        var workflow = h.Load(_twoSteps);
        var baseline = await h.Runner.RunTestAsync(workflow, new WorkflowRunRequest());

        var result = await h.Runner.RunTestAsync(workflow, new WorkflowRunRequest { Observer = observer });

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(baseline.Outputs, result.Outputs);

        Assert.Equal(failOnCall + 1, observer.Calls);
        var warning = Assert.Single(h.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("execution observer failed", StringComparison.Ordinal));
        Assert.IsType<InvalidOperationException>(warning.Exception);
    }

    [Fact]
    public async Task ObserverThrowingCancellation_DoesNotCancelTheRun()
    {
        using var h = new RuntimeHarness();
        var observer = new ThrowingObserver(2, new OperationCanceledException());

        var result = await h.Runner.RunTestAsync(h.Load(_twoSteps), new WorkflowRunRequest { Observer = observer });

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(3, observer.Calls);
    }

    [Fact]
    public async Task FailingObserver_DoesNotChangeAFailingRunsError()
    {
        using var h = new RuntimeHarness();
        var observer = new ThrowingObserver(0, new InvalidOperationException("observer bug"));
        var workflow = h.Load(RuntimeHarness.Workflow("""{ "id": "boom", "type": "Test.Fail", "properties": { "message": "'broken'" } }"""));

        var result = await h.Runner.RunTestAsync(workflow, new WorkflowRunRequest { Observer = observer });

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("boom", result.Error!.NodeId);
        Assert.Contains("broken", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentRuns_EachObserverSeesOnlyItsOwnRun()
    {
        using var h = new RuntimeHarness();
        var workflow = h.Load(_twoSteps);
        var observers = Enumerable.Range(0, 24).Select(_ => new RecordingObserver()).ToList();

        var results = await Task.WhenAll(observers.Select(o => Task.Run(() => h.Runner.RunTestAsync(workflow, new WorkflowRunRequest { Observer = o }), TestContext.Current.CancellationToken)));

        for (var i = 0; i < observers.Count; i++)
        {
            Assert.Equal(8, observers[i].Events.Count);
            Assert.All(observers[i].Events, e => Assert.Equal(results[i].ExecutionId, e.ExecutionId));
            Assert.All(observers[i].Events, e => Assert.Equal(results[i].CorrelationId, e.CorrelationId));
        }

        Assert.Equal(observers.Count, results.Select(r => r.ExecutionId).Distinct().Count());
    }

    [Fact]
    public void Events_CarryNoWorkflowData()
    {
        // ADR-0023: identifiers, types, statuses, errors and timings only; never variable values, arguments or outputs.
        Type[] allowed =
        [
            typeof(ExecutionId), typeof(CorrelationId), typeof(WorkflowId), typeof(NodeId), typeof(ActivityTypeName),
            typeof(DateTimeOffset), typeof(TimeSpan), typeof(ExecutionStatus), typeof(ExecutionError),
        ];
        var eventTypes = typeof(ExecutionEvent).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(ExecutionEvent))).ToList();

        Assert.Equal(4, eventTypes.Count);
        Assert.All(
            eventTypes.SelectMany(t => t.GetProperties()).Where(p => p.Name != "EqualityContract"),
            p => Assert.Contains(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType, allowed));
    }

    internal sealed class RecordingObserver : IExecutionObserver
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _waiting = new();

        public ConcurrentQueue<ExecutionEvent> Events { get; } = new();

        public void OnEvent(ExecutionEvent executionEvent)
        {
            Events.Enqueue(executionEvent);
            _waiting.GetOrAdd(Describe(executionEvent), _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }

        public Task WaitForAsync(string description) =>
            _waiting.GetOrAdd(description, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TestContext.Current.CancellationToken);

        public List<string> Describe() => [.. Events.Select(Describe)];

        private static string Describe(ExecutionEvent e) => e switch
        {
            ExecutionStarted => "ExecutionStarted",
            NodeStarted n => $"NodeStarted {n.NodeId}",
            NodeCompleted n => $"NodeCompleted {n.NodeId} {n.Status}",
            ExecutionCompleted c => $"ExecutionCompleted {c.Status}",
            _ => e.GetType().Name,
        };
    }

    private sealed class ThrowingObserver(int failOnCall, Exception exception) : IExecutionObserver
    {
        public int Calls { get; private set; }

        public void OnEvent(ExecutionEvent executionEvent)
        {
            if (Calls++ == failOnCall)
            {
                throw exception;
            }
        }
    }
}
