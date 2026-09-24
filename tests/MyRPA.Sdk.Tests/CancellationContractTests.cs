using MyRPA.Activities;
using MyRPA.Core.Execution;
using MyRPA.Workflow.Execution;

namespace MyRPA.Sdk.Tests;

/// <summary>
/// ADR-0013 cancellation contract: the token carries cancellation and timeouts, cancellation is cooperative, the engine
/// never abandons a running activity, and bounded cleanup after cancellation is allowed.
/// </summary>
public sealed class CancellationContractTests
{
    private static SdkHarness Harness() => new(s => s
        .AddActivity<CooperativeActivity>(SdkHarness.Descriptor("Test.Cooperative"))
        .AddActivity<StubbornActivity>(SdkHarness.Descriptor("Test.Stubborn"))
        .AddActivity<OwnTimeoutActivity>(SdkHarness.Descriptor("Test.OwnTimeout")));

    private static string Sequence(params string[] types) =>
        $$"""{ "id": "s", "type": "Core.Sequence", "children": [ {{string.Join(", ", types.Select((t, i) => $$"""{ "id": "n{{i}}", "type": "{{t}}" }"""))}} ] }""";

    [Fact]
    public async Task Timeout_CancelsTheToken_AndTheRunReportsTimedOut()
    {
        using var h = Harness();

        var result = await h.RunAsync(SdkHarness.Workflow(Sequence("Test.Cooperative")), timeout: TimeSpan.FromMilliseconds(50));

        Assert.Equal(ExecutionStatus.TimedOut, result.Status);
        Assert.Equal(["waiting", "cleanup-done"], h.Probe.Events);
    }

    [Fact]
    public async Task ExternalCancellation_ReportsCancelled_AndCleanupCompletesFirst()
    {
        using var h = Harness();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var result = await h.RunAsync(SdkHarness.Workflow(Sequence("Test.Cooperative")), cancellationToken: cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        // Cleanup ran (with CancellationToken.None) before the engine returned the result.
        Assert.Equal(["waiting", "cleanup-done"], h.Probe.Events);
    }

    [Fact]
    public async Task ActivityIgnoringTheToken_IsNotAbandoned_AndTheNextNodeIsNotStarted()
    {
        // Cancellation cannot interrupt code that does not observe the token: the engine waits for it, then stops
        // before the next node.
        using var h = Harness();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = h.RunAsync(SdkHarness.Workflow(Sequence("Test.Stubborn", "Test.Cooperative")), cancellationToken: cts.Token);
        await h.Probe.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        var finishedEarly = await Task.WhenAny(run, Task.Delay(200, TestContext.Current.CancellationToken)) == run;
        h.Probe.Release.SetResult();
        var result = await run;

        Assert.False(finishedEarly, "The engine returned before the activity finished.");
        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Equal(["stubborn-finished"], h.Probe.Events);
    }

    [Fact]
    public async Task OperationCanceled_NotCausedByTheRun_IsANodeFailure()
    {
        using var h = Harness();

        var result = await h.RunAsync(SdkHarness.Workflow(Sequence("Test.OwnTimeout")));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("n0", result.Error!.NodeId);
    }

    public sealed class CooperativeActivity(Probe probe) : IActivity
    {
        public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
        {
            probe.Events.Enqueue("waiting");
            try
            {
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
            }
            finally
            {
                // Bounded cleanup after cancellation is allowed and uses its own token.
                await Task.Delay(10, CancellationToken.None);
                probe.Events.Enqueue("cleanup-done");
            }

            return ActivityResult.Completed;
        }
    }

    public sealed class StubbornActivity(Probe probe) : IActivity
    {
        public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
        {
            // Simulates a technology call that cannot be cancelled: it ignores the token until it is released.
            probe.Started.SetResult();
            await probe.Release.Task;
            probe.Events.Enqueue("stubborn-finished");
            return ActivityResult.Completed;
        }
    }

    public sealed class OwnTimeoutActivity : IActivity
    {
        public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
        {
            using var own = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
            await Task.Delay(Timeout.Infinite, own.Token);
            return ActivityResult.Completed;
        }
    }
}
