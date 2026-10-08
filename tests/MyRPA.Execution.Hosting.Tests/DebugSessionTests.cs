using MyRPA.Contracts.Execution;
using MyRPA.Core.Execution;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;

namespace MyRPA.Execution.Hosting.Tests;

/// <summary>Debug sessions (ADR-0040): breakpoints, stepping through sequences, graphs and invoked workflows, pause, stop.</summary>
public sealed class DebugSessionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Workflow(string root, string variables = "[]", string id = "test", string schema = "1.0") =>
        $$"""{ "schemaVersion": "{{schema}}", "id": "{{id}}", "name": "Test", "version": "1.0.0", "variables": {{variables}}, "root": {{root}} }""";

    private static string Log(string id, string transitions = "") =>
        $$"""{ "id": "{{id}}", "type": "Core.Log", "properties": { "message": "'{{id}}'" }{{transitions}} }""";

    private static string Sequence(string id, params string[] children) =>
        $$"""{ "id": "{{id}}", "type": "Core.Sequence", "children": [ {{string.Join(", ", children)}} ] }""";

    private static DebugOptions At(params string[] nodeIds) => new() { Breakpoints = [.. nodeIds.Select(n => new DebugBreakpoint("test", n))] };

    private static async Task<ExecutionEventMessage> NextPauseAsync(IAsyncEnumerator<ExecutionEventMessage> events) =>
        await events.ReadUntilAsync(e => e.Kind == ExecutionEventKinds.DebugPaused);

    [Fact]
    public async Task Breakpoint_PausesBeforeTheNode_ValuesStayOutOfTheEvents()
    {
        await using var h = new HostingHarness();
        var workflow = h.Load(Workflow(
            Sequence(
                "main",
                """{ "id": "set", "type": "Core.Assign", "properties": { "to": "n", "value": "41" } }""",
                Log("say"),
                """{ "id": "inc", "type": "Core.Assign", "properties": { "to": "n", "value": "n + 1" } }"""),
            """[ { "name": "n", "type": "Int", "default": 0 } ]"""));

        var handle = h.Host.Start(workflow, new ExecutionStartRequest { Debug = At("say") });
        await using var events = handle.ReadEventsAsync(0, Token).GetAsyncEnumerator(Token);
        var paused = await NextPauseAsync(events);

        Assert.Equal(("test", "say", "Core.Log", "breakpoint"), (paused.WorkflowId, paused.NodeId, paused.ActivityType, paused.Reason));
        Assert.Null(paused.Message);
        var pause = handle.Debug!.Paused!;
        Assert.Equal(DebugPauseReason.Breakpoint, pause.Reason);
        Assert.Equal([new DebugValue("n", DebugValueKind.Variable, WorkflowDataType.Int, 41L)], pause.Values);

        Assert.True(handle.Debug.Resume(DebugCommand.Continue));
        Assert.True((await handle.Completion.WaitAsync(Token)).Succeeded);
        var all = (await handle.ReadAllAsync()).Select(EventReading.Describe).ToList();
        var at = all.IndexOf(ExecutionEventKinds.DebugPaused);
        Assert.Equal([ExecutionEventKinds.DebugPaused, ExecutionEventKinds.DebugResumed, "node.started say"], all[at..(at + 3)]);
        Assert.Null(handle.Debug.Paused);
    }

    [Fact]
    public async Task Steps_IntoOverAndOut_FollowTheNesting()
    {
        await using var h = new HostingHarness();
        var workflow = h.Load(Workflow(Sequence("main", Log("a"), Sequence("group", Log("b"), Log("c")), Log("d"))));

        var handle = h.Host.Start(workflow, new ExecutionStartRequest { Debug = new DebugOptions { PauseAtStart = true } });
        await using var events = handle.ReadEventsAsync(0, Token).GetAsyncEnumerator(Token);
        var pauses = new List<string?> { (await NextPauseAsync(events)).NodeId };
        foreach (var command in new[] { DebugCommand.StepInto, DebugCommand.StepOver, DebugCommand.StepInto, DebugCommand.StepOut })
        {
            Assert.True(handle.Debug!.Resume(command));
            var paused = await NextPauseAsync(events);
            Assert.Equal("step", paused.Reason);
            pauses.Add(paused.NodeId);
        }

        Assert.True(handle.Debug!.Resume(DebugCommand.StepOver));
        Assert.True((await handle.Completion.WaitAsync(Token)).Succeeded);
        Assert.Equal(["main", "a", "group", "b", "d"], pauses);
    }

    [Fact]
    public async Task StepOver_InAFlowchart_GoesToTheNextStep()
    {
        await using var h = new HostingHarness();
        var flowchart = $$"""
            { "id": "flow", "type": "Core.Flowchart", "children": [
              {{Log("s1", """, "transitions": [ { "to": "s2" } ]""")}},
              {{Log("s2", """, "transitions": [ { "to": "s3" } ]""")}},
              {{Log("s3")}} ] }
            """;
        var handle = h.Host.Start(h.Load(Workflow(flowchart, schema: "1.1")), new ExecutionStartRequest { Debug = At("s1") });
        await using var events = handle.ReadEventsAsync(0, Token).GetAsyncEnumerator(Token);

        var pauses = new List<string?> { (await NextPauseAsync(events)).NodeId };
        Assert.True(handle.Debug!.Resume(DebugCommand.StepOver));
        pauses.Add((await NextPauseAsync(events)).NodeId);
        Assert.True(handle.Debug.Resume(DebugCommand.StepOver));
        pauses.Add((await NextPauseAsync(events)).NodeId);
        Assert.True(handle.Debug.Resume(DebugCommand.Continue));

        Assert.True((await handle.Completion.WaitAsync(Token)).Succeeded);
        Assert.Equal(["s1", "s2", "s3"], pauses);
    }

    [Fact]
    public async Task Breakpoint_InAnInvokedWorkflow_Pauses_AndStepOutReturnsToTheInvoker()
    {
        await using var h = new HostingHarness(workflows: new() { ["child"] = Workflow(Sequence("inner", Log("x"), Log("y")), id: "child") });
        var parent = h.Load(Workflow(Sequence("main", """{ "id": "call", "type": "Core.InvokeWorkflow", "properties": { "workflow": "child" } }""", Log("after"))));

        var handle = h.Host.Start(parent, new ExecutionStartRequest { Location = "parent", Debug = new DebugOptions { Breakpoints = [new("child", "x")] } });
        await using var events = handle.ReadEventsAsync(0, Token).GetAsyncEnumerator(Token);
        var inChild = await NextPauseAsync(events);

        Assert.Equal(("child", "x"), (inChild.WorkflowId, inChild.NodeId));
        Assert.NotNull(inChild.ParentExecutionId);
        Assert.True(handle.Debug!.Resume(DebugCommand.StepOut));
        var back = await NextPauseAsync(events);
        Assert.Equal(("test", "after"), (back.WorkflowId, back.NodeId));
        Assert.Null(back.ParentExecutionId);

        Assert.True(handle.Debug.Resume(DebugCommand.Continue));
        Assert.True((await handle.Completion.WaitAsync(Token)).Succeeded);
    }

    [Fact]
    public async Task Paused_InALoopBody_ShowsTheLoopItemAsALocal()
    {
        await using var h = new HostingHarness();
        var workflow = h.Load(Workflow(
            """{ "id": "loop", "type": "Core.ForEach", "properties": { "items": "items", "itemVariable": "item" }, "slots": { "body": { "id": "body", "type": "Core.Log", "properties": { "message": "item" } } } }""",
            """[ { "name": "items", "type": "List", "default": [ 1, 2 ] } ]"""));

        var handle = h.Host.Start(workflow, new ExecutionStartRequest { Debug = At("body") });
        await using var events = handle.ReadEventsAsync(0, Token).GetAsyncEnumerator(Token);
        var items = new List<object?>();
        for (var i = 0; i < 2; i++)
        {
            await NextPauseAsync(events);
            var local = Assert.Single(handle.Debug!.Paused!.Values, v => v.Kind == DebugValueKind.Local);
            Assert.Equal("item", local.Name);
            items.Add(local.Value);
            Assert.True(handle.Debug.Resume(DebugCommand.Continue));
        }

        Assert.True((await handle.Completion.WaitAsync(Token)).Succeeded);
        Assert.Equal([1L, 2L], items);
    }

    [Fact]
    public async Task RequestPause_PausesBeforeTheNextNode_AfterTheRunningActivity()
    {
        await using var h = new HostingHarness();
        var workflow = h.Load(Workflow(Sequence("main", """{ "id": "wait", "type": "Core.Delay", "properties": { "milliseconds": 1000 } }""", Log("after"))));

        var handle = h.Host.Start(workflow, new ExecutionStartRequest { Debug = new DebugOptions() });
        await using var events = handle.ReadEventsAsync(0, Token).GetAsyncEnumerator(Token);
        await events.ReadUntilAsync(e => e.Kind == ExecutionEventKinds.NodeStarted && e.NodeId == "wait");
        Assert.True(handle.Debug!.RequestPause());

        var pausing = NextPauseAsync(events);
        await h.Time.AdvanceUntilAsync(TimeSpan.FromMilliseconds(100), pausing);
        var paused = await pausing;

        Assert.Equal(("after", "pause"), (paused.NodeId, paused.Reason));
        Assert.False(handle.Debug.RequestPause());
        Assert.True(handle.Debug.Resume(DebugCommand.Continue));
        Assert.True((await handle.Completion.WaitAsync(Token)).Succeeded);
    }

    [Fact]
    public async Task Breakpoints_ChangedWhilePaused_ApplyFromTheNextNode()
    {
        await using var h = new HostingHarness();
        var workflow = h.Load(Workflow(Sequence("main", Log("a"), Log("b"), Log("c"))));

        var handle = h.Host.Start(workflow, new ExecutionStartRequest { Debug = new DebugOptions { PauseAtStart = true, Breakpoints = [new("test", "b")] } });
        await using var events = handle.ReadEventsAsync(0, Token).GetAsyncEnumerator(Token);
        Assert.Equal("pause", (await NextPauseAsync(events)).Reason);
        handle.Debug!.SetBreakpoints([new("test", "c")]);
        Assert.True(handle.Debug.Resume(DebugCommand.Continue));

        var paused = await NextPauseAsync(events);
        Assert.Equal(("c", "breakpoint"), (paused.NodeId, paused.Reason));
        Assert.Equal([new DebugBreakpoint("test", "c")], handle.Debug.Breakpoints);
        Assert.True(handle.Debug.Resume(DebugCommand.Continue));
        Assert.True((await handle.Completion.WaitAsync(Token)).Succeeded);
    }

    [Fact]
    public async Task Cancel_WhilePaused_EndsTheRunAsCancelled_AndCommandsNoLongerApply()
    {
        await using var h = new HostingHarness();
        var workflow = h.Load(Workflow(Sequence("main", Log("a"), Log("b"))));

        var handle = h.Host.Start(workflow, new ExecutionStartRequest { Debug = At("b") });
        await using var events = handle.ReadEventsAsync(0, Token).GetAsyncEnumerator(Token);
        await NextPauseAsync(events);
        Assert.True(h.Host.Cancel(handle.RunId));

        Assert.Equal(ExecutionStatus.Cancelled, (await handle.Completion.WaitAsync(Token)).Status);
        Assert.Null(handle.Debug!.Paused);
        Assert.False(handle.Debug.Resume(DebugCommand.Continue));
        Assert.False(handle.Debug.RequestPause());
        var all = (await handle.ReadAllAsync()).Select(EventReading.Describe).ToList();
        Assert.DoesNotContain("node.started b", all);
        Assert.Equal("execution.completed Cancelled", all[^1]);
    }

    [Fact]
    public async Task Run_WithoutDebug_HasNoSession_AndNoDebugEvents()
    {
        await using var h = new HostingHarness();
        var handle = h.Host.Start(h.Load(Workflow(Sequence("main", Log("a")))), new ExecutionStartRequest());

        Assert.True((await handle.Completion.WaitAsync(Token)).Succeeded);
        Assert.Null(handle.Debug);
        Assert.DoesNotContain(await handle.ReadAllAsync(), e => e.Kind.StartsWith("debug.", StringComparison.Ordinal));
    }
}
