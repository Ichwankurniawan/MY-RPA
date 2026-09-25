using System.Text.Json;
using MyRPA.Contracts.Execution;
using MyRPA.Core.Execution;

namespace MyRPA.Execution.Hosting.Tests;

public sealed class ExecutionHostTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Run_StreamsContiguousEventsAndLogs_InExecutionOrder()
    {
        await using var h = new HostingHarness();
        var handle = h.Host.Start(h.Load(HostingHarness.Logs(2)), new ExecutionStartRequest());

        var result = await handle.Completion.WaitAsync(Token);
        var events = await handle.ReadAllAsync();

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(
            ["execution.started", "node.started main", "node.started l0", "log l0: line 0", "node.completed l0 Succeeded",
             "node.started l1", "log l1: line 1", "node.completed l1 Succeeded", "node.completed main Succeeded", "execution.completed Succeeded"],
            events.Select(EventReading.Describe));
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Sequence));
        Assert.All(events, e => Assert.Equal(handle.RunId, e.RunId));
        Assert.All(events, e => Assert.Equal(result.ExecutionId.ToString(), e.ExecutionId));
        Assert.Equal(result.CorrelationId, handle.CorrelationId);
        Assert.Equal(ExecutionRunState.Completed, handle.State);
        Assert.Equal(events.Count, handle.LastSequence);
    }

    [Fact]
    public async Task Replay_AfterAnySequence_ReturnsExactlyTheRemainder()
    {
        await using var h = new HostingHarness();
        var handle = h.Host.Start(h.Load(HostingHarness.Logs(3)), new ExecutionStartRequest());
        await handle.Completion.WaitAsync(Token);
        var all = await handle.ReadAllAsync();

        for (var after = 0; after <= all.Count; after++)
        {
            var rest = await handle.ReadAllAsync(after);
            Assert.Equal(all.Skip(after).Select(e => e.Sequence), rest.Select(e => e.Sequence));
        }
    }

    [Fact]
    public async Task LiveReader_ReceivesEventsAsTheyHappen_ThenTheTerminalEvent()
    {
        await using var h = new HostingHarness();
        var handle = h.Host.Start(h.Load(HostingHarness.Delay(5000)), new ExecutionStartRequest());
        await using var reader = handle.ReadEventsAsync(0, Token).GetAsyncEnumerator(Token);

        var started = await reader.ReadUntilAsync(e => e.Kind == ExecutionEventKinds.NodeStarted);
        Assert.Equal(ExecutionRunState.Running, handle.State);
        h.Time.Advance(TimeSpan.FromSeconds(5));
        var completed = await reader.ReadUntilAsync(e => e.Kind == ExecutionEventKinds.ExecutionCompleted);

        Assert.Equal("wait", started.NodeId);
        Assert.Equal("Succeeded", completed.Status);
        Assert.False(await reader.MoveNextAsync()); // the stream ends after the terminal event
    }

    [Fact]
    public async Task BoundedBuffer_ReportsTheGap_AndKeepsTheTerminalEvent()
    {
        await using var h = new HostingHarness(o => o.EventBufferCapacity = 8);
        var handle = h.Host.Start(h.Load(HostingHarness.Logs(20)), new ExecutionStartRequest());
        await handle.Completion.WaitAsync(Token);
        var total = handle.LastSequence;

        var events = await handle.ReadAllAsync();

        var gap = events[0];
        Assert.Equal(ExecutionEventKinds.Gap, gap.Kind);
        Assert.Equal(1, gap.MissingFromSequence);
        Assert.Equal(total - 8, gap.MissingToSequence);
        Assert.Equal(Enumerable.Range((int)(total - 7), 8).Select(i => (long)i), events.Skip(1).Select(e => e.Sequence));
        Assert.Equal(ExecutionEventKinds.ExecutionCompleted, events[^1].Kind);
        Assert.Equal(64, total); // 2 execution + 42 node + 20 log events
    }

    [Fact]
    public async Task ConcurrentRuns_EventsAndLogsNeverCrossRuns()
    {
        await using var h = new HostingHarness(o => o.MaxConcurrentExecutions = 8);
        var workflow = h.Load(HostingHarness.Workflow(
            """{ "id": "say", "type": "Core.Log", "properties": { "message": "'run ' + tag" } }""",
            """[ { "name": "tag", "direction": "In", "type": "String", "required": true } ]"""));

        var handles = Enumerable.Range(0, 32)
            .Select(i => h.Host.Start(workflow, new ExecutionStartRequest { Arguments = new Dictionary<string, object?> { ["tag"] = $"t{i}" } }))
            .ToList();
        var results = await Task.WhenAll(handles.Select(x => x.Completion)).WaitAsync(Token);

        for (var i = 0; i < handles.Count; i++)
        {
            var events = await handles[i].ReadAllAsync();
            Assert.Equal($"run t{i}", Assert.Single(events, e => e.Kind == ExecutionEventKinds.Log).Message);
            Assert.All(events, e => Assert.Equal(results[i].ExecutionId.ToString(), e.ExecutionId));
            Assert.All(events, e => Assert.Equal(handles[i].RunId, e.RunId));
        }

        Assert.Equal(handles.Count, handles.Select(x => x.RunId).Distinct().Count());
    }

    [Fact]
    public async Task InvokedWorkflow_EventsAndLogsBelongToTheRun_AndOnlyTheRunsCompletionIsTerminal()
    {
        await using var h = new HostingHarness(workflows: new()
        {
            ["child"] = HostingHarness.Workflow("""{ "id": "inner", "type": "Core.Log", "properties": { "message": "'from child'" } }""", id: "child"),
        });
        var handle = h.Host.Start(
            h.Load(HostingHarness.Workflow("""{ "id": "call", "type": "Core.InvokeWorkflow", "properties": { "workflow": "child" } }""", id: "parent")),
            new ExecutionStartRequest { Location = "parent" });
        var result = await handle.Completion.WaitAsync(Token);

        var events = await handle.ReadAllAsync();

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var childStarted = Assert.Single(events, e => e.Kind == ExecutionEventKinds.ExecutionStarted && e.ParentExecutionId is not null);
        Assert.Equal(result.ExecutionId.ToString(), childStarted.ParentExecutionId);
        Assert.Equal("child", childStarted.WorkflowId);
        var log = Assert.Single(events, e => e.Kind == ExecutionEventKinds.Log);
        Assert.Equal(("from child", "inner", childStarted.ExecutionId), (log.Message, log.NodeId, log.ExecutionId));
        Assert.Equal(2, events.Count(e => e.Kind == ExecutionEventKinds.ExecutionCompleted));
        Assert.Null(events[^1].ParentExecutionId);
        Assert.Equal(result.ExecutionId.ToString(), events[^1].ExecutionId);
    }

    [Fact]
    public async Task Cancel_RunningRun_EndsCancelled_AndCannotBeCancelledTwice()
    {
        await using var h = new HostingHarness();
        var handle = h.Host.Start(h.Load(HostingHarness.Delay(60000)), new ExecutionStartRequest());
        await using var reader = handle.ReadEventsAsync(0, Token).GetAsyncEnumerator(Token);
        await reader.ReadUntilAsync(e => e.Kind == ExecutionEventKinds.NodeStarted);

        Assert.True(h.Host.Cancel(handle.RunId));
        var result = await handle.Completion.WaitAsync(Token);
        var completed = await reader.ReadUntilAsync(e => e.Kind == ExecutionEventKinds.ExecutionCompleted);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Equal("Cancelled", completed.Status);
        Assert.False(h.Host.Cancel(handle.RunId));
    }

    [Fact]
    public async Task ConcurrencyLimit_QueuesRuns_AndCancellingAQueuedRunNeverStartsIt()
    {
        await using var h = new HostingHarness(o => o.MaxConcurrentExecutions = 1);
        var first = h.Host.Start(h.Load(HostingHarness.Delay(1000)), new ExecutionStartRequest());
        await using var reader = first.ReadEventsAsync(0, Token).GetAsyncEnumerator(Token);
        await reader.ReadUntilAsync(e => e.Kind == ExecutionEventKinds.NodeStarted);
        var queued = h.Host.Start(h.Load(HostingHarness.Logs(1)), new ExecutionStartRequest());
        var cancelledWhileQueued = h.Host.Start(h.Load(HostingHarness.Logs(1)), new ExecutionStartRequest());

        Assert.Equal(ExecutionRunState.Queued, queued.State);
        Assert.True(h.Host.Cancel(cancelledWhileQueued.RunId));
        var cancelled = await cancelledWhileQueued.Completion.WaitAsync(Token);
        Assert.Equal(ExecutionStatus.Cancelled, cancelled.Status);
        var only = Assert.Single(await cancelledWhileQueued.ReadAllAsync());
        Assert.Equal(("execution.completed", "Cancelled"), (only.Kind, only.Status));
        Assert.Equal(ExecutionRunState.Queued, queued.State);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ExecutionStatus.Succeeded, (await first.Completion.WaitAsync(Token)).Status);
        Assert.Equal(ExecutionStatus.Succeeded, (await queued.Completion.WaitAsync(Token)).Status);
        Assert.Contains(await queued.ReadAllAsync(), e => e.Kind == ExecutionEventKinds.Log);
    }

    [Fact]
    public async Task SlowOrAbandonedReaders_NeverHoldBackTheRun()
    {
        await using var h = new HostingHarness();
        var handle = h.Host.Start(h.Load(HostingHarness.Logs(500)), new ExecutionStartRequest());
        using var stopReading = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await using (var abandoned = handle.ReadEventsAsync(0, stopReading.Token).GetAsyncEnumerator(stopReading.Token))
        {
            await abandoned.MoveNextAsync();
            await stopReading.CancelAsync();
        }

        var result = await handle.Completion.WaitAsync(Token);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(ExecutionEventKinds.ExecutionCompleted, (await handle.ReadAllAsync())[^1].Kind);
    }

    [Fact]
    public async Task CompletedRuns_BeyondTheRetentionLimit_AreForgotten_ExistingHandlesStillWork()
    {
        await using var h = new HostingHarness(o => o.MaxRetainedExecutions = 2);
        var handles = new List<ExecutionHandle>();
        for (var i = 0; i < 4; i++)
        {
            var handle = h.Host.Start(h.Load(HostingHarness.Logs(1)), new ExecutionStartRequest());
            await handle.Completion.WaitAsync(Token);
            handles.Add(handle);
        }

        Assert.False(h.Host.TryGet(handles[0].RunId, out _));
        Assert.False(h.Host.TryGet(handles[1].RunId, out _));
        Assert.True(h.Host.TryGet(handles[3].RunId, out var latest));
        Assert.Equal(handles[3].LastSequence, latest.LastSequence);
        Assert.NotEmpty(await handles[0].ReadAllAsync());
    }

    [Fact]
    public async Task UnknownRun_IsNotFound()
    {
        await using var h = new HostingHarness();

        Assert.False(h.Host.TryGet("no-such-run", out _));
        Assert.False(h.Host.Cancel("no-such-run"));
    }

    [Fact]
    public async Task DisposingTheHost_CancelsRunningRuns_AndRefusesNewOnes()
    {
        var h = new HostingHarness();
        var host = h.Host;
        var workflow = h.Load(HostingHarness.Delay(60000));
        var handle = host.Start(workflow, new ExecutionStartRequest());
        await using (var reader = handle.ReadEventsAsync(0, Token).GetAsyncEnumerator(Token))
        {
            await reader.ReadUntilAsync(e => e.Kind == ExecutionEventKinds.NodeStarted);
        }

        await h.DisposeAsync();

        Assert.Equal(ExecutionStatus.Cancelled, (await handle.Completion.WaitAsync(Token)).Status);
        Assert.Throws<ObjectDisposedException>(() => host.Start(workflow, new ExecutionStartRequest()));
    }

    [Fact]
    public void Options_AreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostingHarness(o => o.MaxConcurrentExecutions = 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostingHarness(o => o.EventBufferCapacity = 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HostingHarness(o => o.MaxRetainedExecutions = -1));
    }
}

public sealed class ContractsSerializationTests
{
    [Fact]
    public void EventMessage_UsesCamelCase_OmitsNulls_AndRoundTrips()
    {
        var message = new ExecutionEventMessage
        {
            Sequence = 7,
            Kind = ExecutionEventKinds.NodeCompleted,
            RunId = "run-1",
            Time = new DateTimeOffset(2026, 9, 25, 1, 2, 3, TimeSpan.Zero),
            ExecutionId = "e1",
            NodeId = "boom",
            ActivityType = "Core.Throw",
            Status = "Failed",
            Error = new ExecutionErrorMessage("MYRPA2002", "broken", "boom", "Core.Throw", "Throw"),
        };

        var json = JsonSerializer.Serialize(message, ContractsJsonContext.Default.ExecutionEventMessage);
        var back = JsonSerializer.Deserialize(json, ContractsJsonContext.Default.ExecutionEventMessage);

        Assert.StartsWith("""{"sequence":7,"kind":"node.completed","runId":"run-1",""", json, StringComparison.Ordinal);
        Assert.DoesNotContain("parentExecutionId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
        Assert.Equal(message, back);
    }
}
