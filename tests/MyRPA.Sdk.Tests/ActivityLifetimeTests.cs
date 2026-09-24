using MyRPA.Activities;
using MyRPA.Core.Execution;
using MyRPA.Workflow.Execution;

namespace MyRPA.Sdk.Tests;

/// <summary>
/// ADR-0013 activity lifetime — the Phase 2 review concern: an <see cref="IDisposable"/> activity must be created and
/// disposed per invocation, never retained by the run's DI scope until the run ends.
/// </summary>
public sealed class ActivityLifetimeTests
{
    private const string Loop = """
        { "id": "loop", "type": "Core.ForEach", "properties": { "items": "numbers", "itemVariable": "item" },
          "slots": { "body": { "id": "probe", "type": "Test.Disposable" } } }
        """;

    private const string Numbers = """[ { "name": "numbers", "type": "List", "default": [1, 2, 3, 4, 5] } ]""";

    private static SdkHarness Harness() => new(s => s
        .AddActivity<DisposableActivity>(SdkHarness.Descriptor("Test.Disposable"))
        .AddActivity<AsyncDisposableActivity>(SdkHarness.Descriptor("Test.AsyncDisposable"))
        .AddActivity<FailingDisposableActivity>(SdkHarness.Descriptor("Test.FailingDisposable"))
        .AddActivity<ThrowOnDisposeActivity>(SdkHarness.Descriptor("Test.ThrowOnDispose"))
        .AddActivity<CollectActivity>(SdkHarness.Descriptor("Test.Collect"))
        .AddActivity<WaitingDisposableActivity>(SdkHarness.Descriptor("Test.Waiting")));

    [Fact]
    public async Task DisposableActivity_IsDisposedAfterEachInvocation()
    {
        using var h = Harness();

        var result = await h.RunAsync(SdkHarness.Workflow(Loop, variables: Numbers));

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => i % 2 == 0 ? "created" : "disposed"), h.Probe.Events);
        Assert.Equal(1, h.Probe.MaxAlive);
    }

    [Fact]
    public async Task InstancesAreNotRetainedByTheRun()
    {
        // While the run is still executing, earlier instances must already be collectable: nothing (such as the DI
        // scope's disposable tracking) holds on to them.
        using var h = Harness();

        var result = await h.RunAsync(SdkHarness.Workflow(
            $$"""{ "id": "s", "type": "Core.Sequence", "children": [ {{Loop}}, { "id": "gc", "type": "Test.Collect" } ] }""",
            variables: Numbers));

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(5, h.Probe.Instances.Count);
        Assert.Contains("alive-after-collect:0", h.Probe.Events);
    }

    [Fact]
    public async Task AsyncDisposal_IsPreferred()
    {
        using var h = Harness();

        await h.RunAsync(SdkHarness.Workflow("""{ "id": "a", "type": "Test.AsyncDisposable" }"""));

        Assert.Equal(["created", "disposed-async"], h.Probe.Events);
    }

    [Fact]
    public async Task FailingActivity_IsStillDisposed()
    {
        using var h = Harness();

        var result = await h.RunAsync(SdkHarness.Workflow("""{ "id": "f", "type": "Test.FailingDisposable" }"""));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(["created", "disposed"], h.Probe.Events);
    }

    [Fact]
    public async Task CancelledActivity_IsStillDisposed()
    {
        using var h = Harness();

        var run = await h.RunAsync(SdkHarness.Workflow("""{ "id": "w", "type": "Test.Waiting" }"""), timeout: TimeSpan.FromMilliseconds(50));

        Assert.Equal(ExecutionStatus.TimedOut, run.Status);
        Assert.Equal(["created", "disposed"], h.Probe.Events);
    }

    [Fact]
    public async Task DisposeFailure_AfterSuccess_FailsTheNode()
    {
        using var h = Harness();

        var result = await h.RunAsync(SdkHarness.Workflow("""{ "id": "t", "type": "Test.ThrowOnDispose" }"""));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("t", result.Error!.NodeId);
        Assert.Equal("dispose failed", result.Error.Message);
    }

    public sealed class DisposableActivity : IActivity, IDisposable
    {
        private readonly Probe _probe;

        public DisposableActivity(Probe probe)
        {
            _probe = probe;
            _probe.Created(this);
        }

        public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context) => ActivityResult.CompletedTask;

        public void Dispose() => _probe.Disposed("disposed");
    }

    public sealed class AsyncDisposableActivity : IActivity, IAsyncDisposable, IDisposable
    {
        private readonly Probe _probe;

        public AsyncDisposableActivity(Probe probe)
        {
            _probe = probe;
            _probe.Created(this);
        }

        public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context) => ActivityResult.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _probe.Disposed("disposed-async");
            return ValueTask.CompletedTask;
        }

        public void Dispose() => _probe.Disposed("disposed-sync");
    }

    public sealed class FailingDisposableActivity : IActivity, IDisposable
    {
        private readonly Probe _probe;

        public FailingDisposableActivity(Probe probe)
        {
            _probe = probe;
            _probe.Created(this);
        }

        public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context) => throw new InvalidOperationException("fails");

        public void Dispose() => _probe.Disposed("disposed");
    }

    public sealed class WaitingDisposableActivity : IActivity, IDisposable
    {
        private readonly Probe _probe;

        public WaitingDisposableActivity(Probe probe)
        {
            _probe = probe;
            _probe.Created(this);
        }

        public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
        {
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
            return ActivityResult.Completed;
        }

        public void Dispose() => _probe.Disposed("disposed");
    }

    public sealed class ThrowOnDisposeActivity : IActivity, IDisposable
    {
        public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context) => ActivityResult.CompletedTask;

        public void Dispose() => throw new InvalidOperationException("dispose failed");
    }

    public sealed class CollectActivity(Probe probe) : IActivity
    {
        public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
        {
            for (var i = 0; i < 5 && probe.Instances.Any(r => r.IsAlive); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            probe.Events.Enqueue($"alive-after-collect:{probe.Instances.Count(r => r.IsAlive)}");
            return ActivityResult.CompletedTask;
        }
    }
}
