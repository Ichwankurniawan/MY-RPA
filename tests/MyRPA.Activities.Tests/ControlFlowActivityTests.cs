using Microsoft.Extensions.Logging;
using MyRPA.Core.Diagnostics;
using MyRPA.Core.Execution;
using MyRPA.Workflow.Execution;

namespace MyRPA.Activities.Tests;

/// <summary>Each built-in activity executed through the real engine.</summary>
public sealed class ControlFlowActivityTests
{
    private const string TraceVariable = """[ { "name": "trace", "type": "List", "default": [] } ]""";
    private const string TraceOutput = """[ { "name": "out", "direction": "Out", "type": "List" } ]""";

    /// <summary>Appends a marker to the <c>trace</c> variable.</summary>
    private static string Mark(string id, string marker = "") =>
        $$"""{ "id": "{{id}}", "type": "Core.Assign", "properties": { "to": "trace", "value": "append(trace, {{(marker.Length == 0 ? $"'{id}'" : marker)}})" } }""";

    /// <summary>Wraps nodes in a sequence that copies <c>trace</c> to the <c>out</c> argument at the end.</summary>
    private static string Traced(params string[] nodes) =>
        ActivityHarness.Workflow(
            $$"""{ "id": "main", "type": "Core.Sequence", "children": [ {{string.Join(", ", nodes)}}, { "id": "copy", "type": "Core.Assign", "properties": { "to": "out", "value": "trace" } } ] }""",
            TraceOutput,
            TraceVariable);

    private static IReadOnlyList<object?> Out(WorkflowExecutionResult result)
    {
        Assert.True(result.Succeeded, result.Error?.Message);
        return Assert.IsAssignableFrom<IReadOnlyList<object?>>(result.Outputs["out"]);
    }

    [Fact]
    public async Task Sequence_RunsChildrenInOrder()
    {
        using var h = new ActivityHarness();
        Assert.Equal(["a", "b", "c"], Out(await h.RunAsync(Traced(Mark("a"), Mark("b"), Mark("c")))));
    }

    [Fact]
    public async Task Assign_ConvertsToDeclaredType_AndRejectsMismatch()
    {
        using var h = new ActivityHarness();
        var widened = await h.RunAsync(ActivityHarness.Workflow(
            """{ "id": "a", "type": "Core.Assign", "properties": { "to": "price", "value": "3" } }""",
            """[ { "name": "price", "direction": "Out", "type": "Decimal" } ]"""));
        Assert.Equal(3m, widened.Outputs["price"]);

        var mismatch = await h.RunAsync(ActivityHarness.Workflow(
            """{ "id": "a", "type": "Core.Assign", "properties": { "to": "count", "value": "'three'" } }""",
            """[ { "name": "count", "direction": "Out", "type": "Int" } ]"""));
        Assert.Equal(ExecutionStatus.Failed, mismatch.Status);
        Assert.Equal("a", mismatch.Error!.NodeId);
    }

    [Fact]
    public async Task Log_WritesToWorkflowLogCategory_WithLevel()
    {
        using var h = new ActivityHarness();
        await h.RunAsync(ActivityHarness.Workflow("""
            { "id": "main", "type": "Core.Sequence", "children": [
              { "id": "l1", "type": "Core.Log", "properties": { "message": "'total: ' + (1 + 2)" } },
              { "id": "l2", "type": "Core.Log", "properties": { "message": "[1, 2]", "level": "Warning" } } ] }
            """));

        var logs = h.Logs.Entries.Where(e => e.Category == DiagnosticNames.WorkflowLogCategory).ToArray();
        Assert.Equal([(LogLevel.Information, "total: 3"), (LogLevel.Warning, "[1,2]")], logs.Select(e => (e.Level, e.Message)));
    }

    [Fact]
    public async Task Delay_WaitsOnTheInjectedClock()
    {
        using var h = new ActivityHarness();
        var run = h.RunAsync(ActivityHarness.Workflow("""{ "id": "d", "type": "Core.Delay", "properties": { "milliseconds": 5000 } }"""));

        h.Time.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.False(run.IsCompleted);
        h.Time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(ExecutionStatus.Succeeded, (await run).Status);
    }

    [Fact]
    public async Task Delay_NegativeDuration_Fails()
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(ActivityHarness.Workflow("""{ "id": "d", "type": "Core.Delay", "properties": { "milliseconds": "0 - 1" } }"""));
        Assert.Equal(ExecutionStatus.Failed, result.Status);
    }

    [Theory]
    [InlineData("1 < 2", "then")]
    [InlineData("1 > 2", "else")]
    public async Task If_ChoosesBranch(string condition, string expected)
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(Traced($$"""
            { "id": "if", "type": "Core.If", "properties": { "condition": "{{condition}}" },
              "slots": { "then": {{Mark("then")}}, "else": {{Mark("else")}} } }
            """));
        Assert.Equal([expected], Out(result));
    }

    [Fact]
    public async Task If_WithoutElse_AndFalseCondition_DoesNothing_NonBooleanFails()
    {
        using var h = new ActivityHarness();
        Assert.Empty(Out(await h.RunAsync(Traced($$"""{ "id": "if", "type": "Core.If", "properties": { "condition": "false" }, "slots": { "then": {{Mark("then")}} } }"""))));

        var result = await h.RunAsync(Traced($$"""{ "id": "if", "type": "Core.If", "properties": { "condition": "1" }, "slots": { "then": {{Mark("then")}} } }"""));
        Assert.Equal(ExecutionErrorCodes.ExpressionFailed, result.Error!.Code);
        Assert.Equal("if", result.Error.NodeId);
    }

    [Theory]
    [InlineData("'gold'", "gold")]
    [InlineData("1 + 1", "two")]
    [InlineData("'bronze'", "default")]
    public async Task Switch_MatchesCaseByText_OrDefault(string expression, string expected)
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(Traced($$"""
            { "id": "sw", "type": "Core.Switch", "properties": { "expression": "{{expression}}" },
              "slots": { "case:gold": {{Mark("gold")}}, "case:2": {{Mark("two")}}, "default": {{Mark("default")}} } }
            """));
        Assert.Equal([expected], Out(result));
    }

    [Fact]
    public async Task Switch_NoMatchAndNoDefault_DoesNothing()
    {
        using var h = new ActivityHarness();
        Assert.Empty(Out(await h.RunAsync(Traced($$"""{ "id": "sw", "type": "Core.Switch", "properties": { "expression": "'x'" }, "slots": { "case:y": {{Mark("y")}} } }"""))));
    }

    [Fact]
    public async Task While_RepeatsWhileTrue_AndChecksBeforeFirstIteration()
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(Traced(
            $$"""{ "id": "w", "type": "Core.While", "properties": { "condition": "len(trace) < 3" }, "slots": { "body": {{Mark("i", "len(trace)")}} } }""",
            $$"""{ "id": "never", "type": "Core.While", "properties": { "condition": "false" }, "slots": { "body": {{Mark("x")}} } }"""));
        Assert.Equal([0L, 1L, 2L], Out(result));
    }

    [Fact]
    public async Task While_MaxIterations_StopsRunawayLoops()
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(Traced($$"""{ "id": "w", "type": "Core.While", "properties": { "condition": "true", "maxIterations": 5 }, "slots": { "body": {{Mark("i")}} } }"""));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("w", result.Error!.NodeId);
        Assert.Contains("maxIterations (5)", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoWhile_RunsBodyAtLeastOnce()
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(Traced(
            $$"""{ "id": "dw", "type": "Core.DoWhile", "properties": { "condition": "len(trace) < 2" }, "slots": { "body": {{Mark("i")}} } }""",
            $$"""{ "id": "dw-once", "type": "Core.DoWhile", "properties": { "condition": "false" }, "slots": { "body": {{Mark("once")}} } }"""));
        Assert.Equal(["i", "i", "once"], Out(result));
    }

    [Fact]
    public async Task ForEach_ExposesItemAndIndexLocals()
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(Traced($$"""
            { "id": "fe", "type": "Core.ForEach", "properties": { "items": "['a', 'b']", "itemVariable": "item", "indexVariable": "i" },
              "slots": { "body": {{Mark("add", "item + i")}} } }
            """));
        Assert.Equal(["a0", "b1"], Out(result));
    }

    [Fact]
    public async Task ForEach_EmptyList_RunsNothing_NonListFails()
    {
        using var h = new ActivityHarness();
        Assert.Empty(Out(await h.RunAsync(Traced($$"""{ "id": "fe", "type": "Core.ForEach", "properties": { "items": "[]", "itemVariable": "x" }, "slots": { "body": {{Mark("x")}} } }"""))));

        var result = await h.RunAsync(Traced($$"""{ "id": "fe", "type": "Core.ForEach", "properties": { "items": "'abc'", "itemVariable": "x" }, "slots": { "body": {{Mark("x")}} } }"""));
        Assert.Equal("fe", result.Error!.NodeId);
    }

    [Fact]
    public async Task TryCatch_CatchesFailure_ExposesError_AndRunsFinally()
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(Traced($$"""
            { "id": "tc", "type": "Core.TryCatch", "properties": { "exceptionVariable": "err" },
              "slots": {
                "try": { "id": "t", "type": "Core.Sequence", "children": [ {{Mark("before")}}, { "id": "boom", "type": "Core.Throw", "properties": { "message": "'bad input'" } }, {{Mark("after")}} ] },
                "catch": {{Mark("caught", "err.message + '|' + err.code + '|' + err.nodeId + '|' + err.activityType + '|' + err.errorType")}},
                "finally": {{Mark("finally")}} } }
            """));

        Assert.Equal(["before", "bad input|MYRPA2002|boom|Core.Throw|Throw", "finally"], Out(result));
    }

    [Fact]
    public async Task TryCatch_Success_RunsFinallyOnly()
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(Traced($$"""
            { "id": "tc", "type": "Core.TryCatch", "slots": { "try": {{Mark("ok")}}, "catch": {{Mark("caught")}}, "finally": {{Mark("finally")}} } }
            """));
        Assert.Equal(["ok", "finally"], Out(result));
    }

    [Fact]
    public async Task TryCatch_WithoutCatch_RunsFinally_ThenPropagates()
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(ActivityHarness.Workflow(
            """
            { "id": "tc", "type": "Core.TryCatch",
              "slots": { "try": { "id": "boom", "type": "Core.Throw", "properties": { "message": "'no handler'" } },
                         "finally": { "id": "f", "type": "Core.Log", "properties": { "message": "'finally ran'" } } } }
            """));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("boom", result.Error!.NodeId);
        Assert.Contains(h.Logs.Entries, e => e.Message == "finally ran");
    }

    [Fact]
    public async Task TryCatch_DoesNotCatchCancellation()
    {
        using var h = new ActivityHarness();
        using var cts = new CancellationTokenSource();
        var run = h.RunAsync(
            Traced($$"""
                { "id": "tc", "type": "Core.TryCatch",
                  "slots": { "try": { "id": "d", "type": "Core.Delay", "properties": { "milliseconds": 60000 } },
                             "catch": {{Mark("caught")}}, "finally": {{Mark("finally")}} } }
                """),
            cancellationToken: cts.Token);

        await cts.CancelAsync();
        var result = await run;

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task Throw_FailsWithUserMessageAndCode()
    {
        using var h = new ActivityHarness();
        var result = await h.RunAsync(ActivityHarness.Workflow("""{ "id": "t", "type": "Core.Throw", "properties": { "message": "'Invoice ' + 42 + ' rejected'" } }"""));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(new ExecutionError(ExecutionErrorCodes.WorkflowThrow, "Invoice 42 rejected", "t", "Core.Throw", "Throw"), result.Error);
    }
}
