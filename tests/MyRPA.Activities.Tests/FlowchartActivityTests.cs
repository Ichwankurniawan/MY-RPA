using MyRPA.Core.Execution;
using MyRPA.Workflow.Execution;

namespace MyRPA.Activities.Tests;

/// <summary><c>Core.Flowchart</c> and <c>Core.Decision</c> through the real engine (ADR-0037).</summary>
public sealed class FlowchartActivityTests
{
    private const string Variables = """[ { "name": "trace", "type": "List", "default": [] }, { "name": "n", "type": "Int", "default": 0 } ]""";
    private const string TraceOutput = """[ { "name": "out", "direction": "Out", "type": "List" } ]""";

    /// <summary>Appends the id to <c>trace</c>; <paramref name="transitions"/> only for flowchart steps.</summary>
    private static string Mark(string id, string? transitions = null) =>
        $$"""{ "id": "{{id}}", "type": "Core.Assign", "properties": { "to": "trace", "value": "append(trace, '{{id}}')" }{{(transitions is null ? "" : $", \"transitions\": {transitions}")}} }""";

    /// <summary>A Sequence holding the flowchart, then copying <c>trace</c> to <c>out</c>; schema 1.1.</summary>
    private static string Workflow(string flowchart) =>
        ActivityHarness.Workflow(
            $$"""{ "id": "main", "type": "Core.Sequence", "children": [ {{flowchart}}, { "id": "copy", "type": "Core.Assign", "properties": { "to": "out", "value": "trace" } } ] }""",
            TraceOutput,
            Variables)
            .Replace("\"schemaVersion\": \"1.0\"", "\"schemaVersion\": \"1.1\"", StringComparison.Ordinal);

    private static string Flowchart(string steps, string properties = "{}") =>
        $$"""{ "id": "flow", "type": "Core.Flowchart", "properties": {{properties}}, "children": [ {{steps}} ] }""";

    private static IReadOnlyList<object?> Out(WorkflowExecutionResult result)
    {
        Assert.True(result.Succeeded, result.Error?.Message);
        return Assert.IsAssignableFrom<IReadOnlyList<object?>>(result.Outputs["out"]);
    }

    [Fact]
    public async Task Flowchart_StartsAtTheFirstStep_AndFollowsTransitions_ThroughADecision()
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(Workflow(Flowchart(string.Join(", ",
            Mark("start", """[ { "to": "decide" } ]"""),
            """{ "id": "decide", "type": "Core.Decision", "transitions": [ { "to": "no", "when": "len(trace) > 5", "label": "many" }, { "to": "yes", "label": "few" } ] }""",
            Mark("no"),
            Mark("yes")))));

        Assert.Equal(["start", "yes"], Out(result));
    }

    [Fact]
    public async Task Flowchart_LoopsBack_UntilTheConditionChanges()
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(Workflow(Flowchart(string.Join(", ",
            Mark("try", """[ { "to": "count" } ]"""),
            """{ "id": "count", "type": "Core.Assign", "properties": { "to": "n", "value": "n + 1" }, "transitions": [ { "to": "try", "when": "n < 3" }, { "to": "done" } ] }""",
            Mark("done")))));

        Assert.Equal(["try", "try", "try", "done"], Out(result));
    }

    [Fact]
    public async Task Flowchart_StepCanBeASequence_AndASequenceStepRunsItsChildren()
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(Workflow(Flowchart(
            $$"""{ "id": "block", "type": "Core.Sequence", "children": [ {{Mark("one")}}, {{Mark("two")}} ], "transitions": [ { "to": "end" } ] }, {{Mark("end")}}""")));

        Assert.Equal(["one", "two", "end"], Out(result));
    }

    [Fact]
    public async Task Flowchart_ExceedingMaxSteps_FailsWithMyrpa2010()
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(Workflow(Flowchart(Mark("again", """[ { "to": "again" } ]"""), """{ "maxSteps": 5 }""")));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(ExecutionErrorCodes.MaxStepsExceeded, result.Error!.Code);
        Assert.Equal("flow", result.Error.NodeId);
        Assert.Equal("Graph", result.Error.ErrorType);
        Assert.Contains("exceeded maxSteps (5)", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flowchart_NegativeMaxSteps_Fails()
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(Workflow(Flowchart(Mark("a"), """{ "maxSteps": -1 }""")));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("maxSteps must not be negative", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flowchart_FailureInAStep_CanBeCaughtAroundTheFlowchart()
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(Workflow($$"""
            { "id": "tc", "type": "Core.TryCatch", "properties": { "exceptionVariable": "err" },
              "slots": {
                "try": {{Flowchart("""{ "id": "boom", "type": "Core.Throw", "properties": { "message": "'stop'" } }""")}},
                "catch": { "id": "caught", "type": "Core.Assign", "properties": { "to": "trace", "value": "append(trace, err.nodeId)" } } } }
            """));

        Assert.Equal(["boom"], Out(result));
    }

    [Fact]
    public async Task Flowchart_Cancellation_StopsTheRun()
    {
        using var h = new ActivityHarness();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = h.RunAsync(
            Workflow(Flowchart("""{ "id": "wait", "type": "Core.Delay", "properties": { "milliseconds": 60000 }, "transitions": [ { "to": "wait" } ] }""")),
            cancellationToken: cts.Token);

        await cts.CancelAsync();

        Assert.Equal(ExecutionStatus.Cancelled, (await run).Status);
    }
}
