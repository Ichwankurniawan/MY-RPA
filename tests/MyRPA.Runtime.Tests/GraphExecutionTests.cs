using MyRPA.Core.Execution;
using MyRPA.Workflow.Execution;

namespace MyRPA.Runtime.Tests;

/// <summary>Graph semantics owned by the engine: <c>IActivityContext.ExecuteStepAsync</c> (SDK 1.1, ADR-0037).</summary>
public sealed class GraphExecutionTests
{
    private const string Trace = """[ { "name": "trace", "type": "List", "default": [] }, { "name": "n", "type": "Int", "default": 0 } ]""";

    private static string Graph(string steps, string extra = "") =>
        RuntimeHarness.Workflow($$"""{ "id": "g", "type": "Test.Graph", "children": [ {{steps}} ]{{extra}} }""", variables: Trace)
            .Replace("\"schemaVersion\": \"1.0\"", "\"schemaVersion\": \"1.1\"", StringComparison.Ordinal);

    private static string Step(string id, string transitions = "[]", string value = "") =>
        $$"""{ "id": "{{id}}", "type": "Test.Set", "properties": { "to": "trace", "value": "{{(value.Length == 0 ? $"append(trace, '{id}')" : value)}}" }, "transitions": {{transitions}} }""";

    private static async Task<(WorkflowExecutionResult Result, List<string> Events)> RunAsync(string json)
    {
        using var h = new RuntimeHarness();
        var observer = new ExecutionEventTests.RecordingObserver();
        var result = await h.Runner.RunTestAsync(h.Load(json), new WorkflowRunRequest { Observer = observer });
        return (result, observer.Describe());
    }

    [Fact]
    public async Task ExecuteStep_TakesTheFirstTransitionWhoseConditionIsTrue()
    {
        var (result, events) = await RunAsync(Graph(string.Join(", ",
            Step("a", """[ { "to": "x", "when": "false" }, { "to": "b", "when": "len(trace) == 1" }, { "to": "x" } ]"""),
            Step("b"),
            Step("x"))));

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(
            ["ExecutionStarted", "NodeStarted g", "NodeStarted a", "NodeCompleted a Succeeded", "NodeStarted b", "NodeCompleted b Succeeded", "NodeCompleted g Succeeded", "ExecutionCompleted Succeeded"],
            events);
    }

    [Fact]
    public async Task ExecuteStep_TransitionWithoutCondition_IsAlwaysTaken_AndNoTransitionEndsTheGraph()
    {
        var (result, events) = await RunAsync(Graph(string.Join(", ", Step("a", """[ { "to": "c" } ]"""), Step("b"), Step("c"))));

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(["g", "a", "c"], events.Where(e => e.StartsWith("NodeStarted", StringComparison.Ordinal)).Select(e => e[12..]));
    }

    [Fact]
    public async Task ExecuteStep_LoopsBack_AndEveryRunOfAStepEmitsItsEvents()
    {
        var (result, events) = await RunAsync(Graph(
            Step("inc", """[ { "to": "inc", "when": "n < 3" } ]""", value: "n + 1").Replace("\"to\": \"trace\", \"value\"", "\"to\": \"n\", \"value\"", StringComparison.Ordinal)));

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(3, events.Count(e => e == "NodeStarted inc"));
        Assert.Equal(3, events.Count(e => e == "NodeCompleted inc Succeeded"));
    }

    [Fact]
    public async Task ExecuteStep_ConditionThatIsNotBoolean_FailsTheContainer()
    {
        var (result, _) = await RunAsync(Graph(string.Join(", ", Step("a", """[ { "to": "b", "when": "'yes'" } ]"""), Step("b"))));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(ExecutionErrorCodes.ExpressionFailed, result.Error!.Code);
        Assert.Equal("g", result.Error.NodeId);
        Assert.Contains("Transition 0 of step 'a'", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteStep_FailingStep_IsAttributedToTheStep()
    {
        var (result, events) = await RunAsync(Graph(
            """{ "id": "boom", "type": "Test.Fail", "properties": { "message": "'broken'" }, "transitions": [ { "to": "after" } ] }, """ + Step("after")));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("boom", result.Error!.NodeId);
        Assert.DoesNotContain("NodeStarted after", events);
    }

    [Fact]
    public async Task ExecuteStep_RefusesANodeThatIsNotAChild()
    {
        var (result, events) = await RunAsync(Graph(Step("a"), extra: """, "slots": { "misuse": { "id": "slot", "type": "Test.Set", "properties": { "to": "n", "value": "1" } } }"""));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("g", result.Error!.NodeId);
        Assert.Contains("'slot' is not a step (child) of node 'g'", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("NodeStarted slot", events);
    }

    [Fact]
    public async Task ChooseTransition_DecidesBeforeTheStepFinishes_AndTheContainerUsesThatChoice()
    {
        // The condition sees n = 0 when the step chooses; the step then sets n = 99. Evaluated after the step, it would fail.
        var (result, events) = await RunAsync(Graph(string.Join(", ",
            """{ "id": "a", "type": "Test.Choose", "properties": { "after": "n" }, "transitions": [ { "to": "b", "when": "n == 0" }, { "to": "c" } ] }""",
            Step("b"),
            Step("c"))));

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal(["g", "a", "b"], events.Where(e => e.StartsWith("NodeStarted", StringComparison.Ordinal)).Select(e => e[12..]));
    }

    [Fact]
    public async Task ChooseTransition_OutsideAGraphStep_OrTwice_IsRefused()
    {
        var outside = await RunAsync(RuntimeHarness.Workflow("""{ "id": "s", "type": "Test.Sequence", "children": [ { "id": "c", "type": "Test.Choose" } ] }""", variables: Trace));
        Assert.Equal(ExecutionStatus.Failed, outside.Result.Status);
        Assert.Contains("'c' is not a step being run by a graph container", outside.Result.Error!.Message, StringComparison.Ordinal);

        var twice = await RunAsync(Graph("""{ "id": "c", "type": "Test.Choose", "properties": { "again": "yes" } }"""));
        Assert.Equal(ExecutionStatus.Failed, twice.Result.Status);
        Assert.Contains("'c' already chose its transition", twice.Result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteStep_Cancellation_PropagatesFromAStep()
    {
        using var h = new RuntimeHarness();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var observer = new ExecutionEventTests.RecordingObserver();
        var workflow = h.Load(Graph("""{ "id": "wait", "type": "Test.Wait", "properties": { "milliseconds": 60000 }, "transitions": [ { "to": "wait" } ] }"""));

        var run = h.Runner.RunTestAsync(workflow, new WorkflowRunRequest { Observer = observer }, cts.Token);
        await observer.WaitForAsync("NodeStarted wait");
        await cts.CancelAsync();
        var result = await run;

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Equal(["NodeCompleted wait Cancelled", "NodeCompleted g Cancelled"], observer.Describe().Where(e => e.StartsWith("NodeCompleted", StringComparison.Ordinal)));
    }
}
