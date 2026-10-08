using MyRPA.Core.Execution;
using MyRPA.Workflow.Execution;

namespace MyRPA.Activities.Tests;

/// <summary><c>Core.StateMachine</c> and <c>Core.State</c> through the real engine (ADR-0037, G-3).</summary>
public sealed class StateMachineActivityTests
{
    private const string Variables = """[ { "name": "trace", "type": "List", "default": [] }, { "name": "n", "type": "Int", "default": 0 } ]""";
    private const string TraceOutput = """[ { "name": "out", "direction": "Out", "type": "List" } ]""";

    private static string Mark(string id, string text) =>
        $$"""{ "id": "{{id}}", "type": "Core.Assign", "properties": { "to": "trace", "value": "append(trace, '{{text}}')" } }""";

    /// <summary>A state with optional entry and exit marks, properties and transitions.</summary>
    private static string State(string id, string transitions = "[]", string properties = "{}", bool entry = true, bool exit = true)
    {
        var slots = new List<string>();
        if (entry)
        {
            slots.Add($"\"entry\": {Mark($"{id}-in", $"{id} entry")}");
        }

        if (exit)
        {
            slots.Add($"\"exit\": {Mark($"{id}-out", $"{id} exit")}");
        }

        return $$"""{ "id": "{{id}}", "type": "Core.State", "properties": {{properties}}, "slots": { {{string.Join(", ", slots)}} }, "transitions": {{transitions}} }""";
    }

    private static string Workflow(string states, string properties = "{}") =>
        ActivityHarness.Workflow(
            $$"""{ "id": "main", "type": "Core.Sequence", "children": [ { "id": "machine", "type": "Core.StateMachine", "properties": {{properties}}, "children": [ {{states}} ] }, { "id": "copy", "type": "Core.Assign", "properties": { "to": "out", "value": "trace" } } ] }""",
            TraceOutput,
            Variables)
            .Replace("\"schemaVersion\": \"1.0\"", "\"schemaVersion\": \"1.1\"", StringComparison.Ordinal);

    private static IReadOnlyList<object?> Out(WorkflowExecutionResult result)
    {
        Assert.True(result.Succeeded, result.Error?.Message);
        return Assert.IsAssignableFrom<IReadOnlyList<object?>>(result.Outputs["out"]);
    }

    [Fact]
    public async Task StateMachine_RunsEntry_ChoosesTheNextState_ThenRunsExit_UntilAFinalState()
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(Workflow(string.Join(", ",
            State("init", """[ { "to": "work" } ]"""),
            State("work", """[ { "to": "work", "when": "len(trace) < 5" }, { "to": "done" } ]"""),
            State("done", properties: """{ "final": true }"""))));

        Assert.Equal(["init entry", "init exit", "work entry", "work exit", "work entry", "work exit", "done entry", "done exit"], Out(result));
    }

    [Fact]
    public async Task State_TransitionsAreCheckedAfterEntry_AndBeforeExit()
    {
        // Exit changes n; the condition must see the value entry left (ADR-0037: entry → transitions → exit).
        using var h = new ActivityHarness();
        var result = await h.RunAsync(Workflow(string.Join(", ",
            """{ "id": "s", "type": "Core.State", "slots": { "exit": { "id": "bump", "type": "Core.Assign", "properties": { "to": "n", "value": "n + 1" } } }, "transitions": [ { "to": "zero", "when": "n == 0" }, { "to": "one" } ] }""",
            State("zero", properties: """{ "final": true }""", exit: false),
            State("one", properties: """{ "final": true }""", exit: false))));

        Assert.Equal(["zero entry"], Out(result));
    }

    [Fact]
    public async Task State_NoTransitionTaken_FromAStateThatIsNotFinal_FailsWithMyrpa2011()
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(Workflow(string.Join(", ", State("stuck", """[ { "to": "end", "when": "false" } ]"""), State("end", properties: """{ "final": true }"""))));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(ExecutionErrorCodes.StateMachineStuck, result.Error!.Code);
        Assert.Equal("stuck", result.Error.NodeId);
        Assert.Equal("Graph", result.Error.ErrorType);
    }

    [Fact]
    public async Task State_FinalByExpression_WithTransitions_FailsAtRunTime()
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(Workflow(State("odd", """[ { "to": "odd" } ]""", """{ "final": "n == 0" }""")));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(ExecutionErrorCodes.StateMachineStuck, result.Error!.Code);
        Assert.Contains("is final, so it cannot have transitions", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StateMachine_ExceedingMaxSteps_FailsWithMyrpa2010()
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(Workflow(State("loop", """[ { "to": "loop" } ]""", entry: false, exit: false), """{ "maxSteps": 4 }"""));

        Assert.Equal(ExecutionErrorCodes.MaxStepsExceeded, result.Error!.Code);
        Assert.Equal("machine", result.Error.NodeId);
    }

    [Fact]
    public async Task StateMachine_Cancellation_StopsTheRun()
    {
        using var h = new ActivityHarness();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = h.RunAsync(
            Workflow("""{ "id": "wait", "type": "Core.State", "slots": { "entry": { "id": "d", "type": "Core.Delay", "properties": { "milliseconds": 60000 } } }, "transitions": [ { "to": "wait" } ] }"""),
            cancellationToken: cts.Token);

        await cts.CancelAsync();

        Assert.Equal(ExecutionStatus.Cancelled, (await run).Status);
    }
}
