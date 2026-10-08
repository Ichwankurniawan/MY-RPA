using MyRPA.Core.Execution;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;

namespace MyRPA.Runtime.Tests;

/// <summary>The engine's debug hook (ADR-0040): asked before every node, pauses suspend timeouts, no hook changes nothing.</summary>
public sealed class DebuggerTests
{
    private const string Variables = """[ { "name": "n", "type": "Int", "default": 0 } ]""";

    private static readonly Dictionary<string, string> _child = new()
    {
        ["child"] = RuntimeHarness.Workflow("""{ "id": "c", "type": "Test.Sequence", "children": [ { "id": "d", "type": "Test.Log", "properties": { "message": "'hi'" } } ] }""", id: "child"),
    };

    // Sequence, nested sequence, graph steps and an invoked workflow.
    private static readonly string _nested = RuntimeHarness.Workflow(
        """
        { "id": "main", "type": "Test.Sequence", "children": [
          { "id": "a", "type": "Test.Set", "properties": { "to": "n", "value": "n + 1" } },
          { "id": "inner", "type": "Test.Sequence", "children": [ { "id": "b", "type": "Test.Set", "properties": { "to": "n", "value": "n + 1" } } ] },
          { "id": "g", "type": "Test.Graph", "children": [
            { "id": "s1", "type": "Test.Set", "properties": { "to": "n", "value": "n + 1" }, "transitions": [ { "to": "s2" } ] },
            { "id": "s2", "type": "Test.Set", "properties": { "to": "n", "value": "n + 1" } } ] },
          { "id": "call", "type": "Test.Invoke", "properties": { "workflow": "child" } } ] }
        """,
        arguments: """[ { "name": "who", "direction": "In", "type": "String", "default": "Ada" } ]""",
        variables: Variables).Replace("\"schemaVersion\": \"1.0\"", "\"schemaVersion\": \"1.1\"", StringComparison.Ordinal);

    private static string Waiting(int milliseconds) => RuntimeHarness.Workflow(
        $$"""{ "id": "main", "type": "Test.Sequence", "children": [ { "id": "a", "type": "Test.Set", "properties": { "to": "n", "value": "1" } }, { "id": "w", "type": "Test.Wait", "properties": { "milliseconds": {{milliseconds}} } } ] }""",
        variables: Variables);

    [Fact]
    public async Task Debugger_IsAskedBeforeEveryNode_WithItsDepthInTheWholeRun()
    {
        using var h = new RuntimeHarness(_child);
        var debugger = new ScriptedDebugger();

        var result = await h.Runner.RunTestAsync(h.Load(_nested), new WorkflowRunRequest { Location = "parent", Debugger = debugger });

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(
            [("main", 0), ("a", 1), ("inner", 1), ("b", 2), ("g", 1), ("s1", 2), ("s2", 2), ("call", 1), ("c", 2), ("d", 3)],
            debugger.Stops);
        Assert.Equal(["test", "test", "test", "test", "test", "test", "test", "test", "child", "child"], debugger.Workflows);
    }

    [Fact]
    public async Task Debugger_ThatNeverPauses_LeavesTheRunUnchanged()
    {
        using var h = new RuntimeHarness(_child);
        var plain = new ExecutionEventTests.RecordingObserver();
        var debugged = new ExecutionEventTests.RecordingObserver();

        var first = await h.Runner.RunTestAsync(h.Load(_nested), new WorkflowRunRequest { Location = "parent", Observer = plain });
        var second = await h.Runner.RunTestAsync(h.Load(_nested), new WorkflowRunRequest { Location = "parent", Observer = debugged, Debugger = new ScriptedDebugger() });

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(plain.Describe(), debugged.Describe());
    }

    [Fact]
    public async Task Pause_HoldsTheNodeBeforeItStarts_AndReadsTheValuesInScope()
    {
        using var h = new RuntimeHarness(_child);
        var observer = new ExecutionEventTests.RecordingObserver();
        var debugger = new ScriptedDebugger("b");

        var run = h.Runner.RunTestAsync(h.Load(_nested), new WorkflowRunRequest { Location = "parent", Observer = observer, Debugger = debugger });
        await debugger.Paused.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(run.IsCompleted);
        Assert.DoesNotContain("NodeStarted b", observer.Describe());
        Assert.Equal(
            [new DebugValue("who", DebugValueKind.Argument, WorkflowDataType.String, "Ada"), new DebugValue("n", DebugValueKind.Variable, WorkflowDataType.Int, 1L)],
            debugger.Values.Single());

        debugger.Release.TrySetResult();
        Assert.True((await run).Succeeded);
        Assert.Contains("NodeStarted b", observer.Describe());
    }

    [Fact]
    public async Task Pause_DoesNotCountTowardsTheTimeout()
    {
        using var h = new RuntimeHarness();
        var debugger = new ScriptedDebugger("w");

        var run = h.Runner.RunTestAsync(h.Load(Waiting(500)), new WorkflowRunRequest { Timeout = TimeSpan.FromSeconds(1), Debugger = debugger });
        await debugger.Paused.Task.WaitAsync(TestContext.Current.CancellationToken);
        h.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted);

        debugger.Release.TrySetResult();
        await AdvanceUntilAsync(h, run);

        Assert.Equal(ExecutionStatus.Succeeded, (await run).Status);
    }

    [Fact]
    public async Task Pause_ResumesTheTimeoutWithItsRemainingTime()
    {
        using var h = new RuntimeHarness();
        var debugger = new ScriptedDebugger("w");

        var run = h.Runner.RunTestAsync(h.Load(Waiting(1500)), new WorkflowRunRequest { Timeout = TimeSpan.FromSeconds(1), Debugger = debugger });
        await debugger.Paused.Task.WaitAsync(TestContext.Current.CancellationToken);
        h.Time.Advance(TimeSpan.FromSeconds(5));
        debugger.Release.TrySetResult();
        await AdvanceUntilAsync(h, run);

        var result = await run;
        Assert.Equal(ExecutionStatus.TimedOut, result.Status);
        Assert.Contains("1000 ms", result.Error!.Message, StringComparison.Ordinal);
        Assert.True(result.Duration >= TimeSpan.FromSeconds(6), $"timed out after {result.Duration}, before the paused 5 s plus the 1 s timeout");
    }

    [Fact]
    public async Task Cancel_WhilePaused_EndsTheRunAsCancelled_WithoutStartingTheNode()
    {
        using var h = new RuntimeHarness(_child);
        var observer = new ExecutionEventTests.RecordingObserver();
        var debugger = new ScriptedDebugger("b");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = h.Runner.RunTestAsync(h.Load(_nested), new WorkflowRunRequest { Location = "parent", Observer = observer, Debugger = debugger }, cancellation.Token);
        await debugger.Paused.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        Assert.Equal(ExecutionStatus.Cancelled, (await run).Status);
        Assert.DoesNotContain("NodeStarted b", observer.Describe());
        Assert.Contains("NodeCompleted inner Cancelled", observer.Describe());
    }

    /// <summary>Steps the fake clock until the run ends (after a pause the run continues on another thread).</summary>
    private static async Task AdvanceUntilAsync(RuntimeHarness h, Task run)
    {
        for (var i = 0; i < 1000 && !run.IsCompleted; i++)
        {
            h.Time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.WhenAny(run, Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken));
        }
    }

    /// <summary>Records every stop; pauses (until <see cref="Release"/>) before the named nodes.</summary>
    private sealed class ScriptedDebugger(params string[] pauseBefore) : IExecutionDebugger
    {
        private readonly HashSet<string> _pauseBefore = [.. pauseBefore];

        public List<(string Node, int Depth)> Stops { get; } = [];

        public List<string> Workflows { get; } = [];

        public List<IReadOnlyList<DebugValue>> Values { get; } = [];

        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask BeforeNodeAsync(DebugStop at, CancellationToken cancellationToken)
        {
            Stops.Add((at.Node.Id.Value, at.Depth));
            Workflows.Add(at.WorkflowId.Value);
            if (!_pauseBefore.Contains(at.Node.Id.Value))
            {
                return ValueTask.CompletedTask;
            }

            Values.Add(at.ReadValues());
            Paused.TrySetResult();
            return new ValueTask(Release.Task.WaitAsync(cancellationToken));
        }
    }
}
