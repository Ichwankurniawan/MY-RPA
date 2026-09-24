using System.Diagnostics;
using MyRPA.Core.Activities;
using MyRPA.Core.Diagnostics;
using MyRPA.Core.Execution;
using MyRPA.Core.Identifiers;
using MyRPA.Workflow;
using MyRPA.Workflow.Execution;

namespace MyRPA.Runtime.Tests;

public sealed class WorkflowRunnerTests
{
    private static readonly string _arguments = """
        [
          { "name": "input", "direction": "In", "type": "Int", "default": 1 },
          { "name": "name", "direction": "In", "type": "String", "required": true },
          { "name": "result", "direction": "Out", "type": "Int" },
          { "name": "counter", "direction": "InOut", "type": "Decimal", "default": 0 }
        ]
        """;

    private static CorrelationId NewCorrelation() => new("test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Run_Success_ReturnsOutputsIdsAndTiming()
    {
        using var h = new RuntimeHarness();
        var workflow = h.Load(RuntimeHarness.Workflow(
            """
            { "id": "main", "type": "Test.Sequence", "children": [
              { "id": "wait", "type": "Test.Wait", "properties": { "milliseconds": 1500 } },
              { "id": "set", "type": "Test.Set", "properties": { "to": "result", "value": "input * 2" } },
              { "id": "inc", "type": "Test.Set", "properties": { "to": "counter", "value": "counter + 1" } } ] }
            """,
            _arguments));

        var run = h.Runner.RunTestAsync(workflow, new WorkflowRunRequest
        {
            Arguments = new Dictionary<string, object?> { ["input"] = 21, ["name"] = "x" },
            CorrelationId = new CorrelationId("job-7"),
        });
        h.Time.Advance(TimeSpan.FromMilliseconds(1500));
        var result = await run;

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Null(result.Error);
        Assert.Equal(42L, result.Outputs["result"]);
        Assert.Equal(1m, result.Outputs["counter"]);
        Assert.Equal(2, result.Outputs.Count);
        Assert.Equal("00000001000000000000000000000000", result.ExecutionId.ToString());
        Assert.Equal("job-7", result.CorrelationId.Value);
        Assert.Equal("test", result.WorkflowId.Value);
        Assert.Equal(RuntimeHarness.Start, result.StartedAt);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), result.Duration);
    }

    [Fact]
    public async Task Run_WithoutCorrelation_GeneratesOne()
    {
        using var h = new RuntimeHarness();
        var workflow = h.Load(RuntimeHarness.Workflow("""{ "id": "noop", "type": "Test.Sequence" }"""));

        var result = await h.Runner.RunTestAsync(workflow, new WorkflowRunRequest());

        Assert.Equal("corr-1", result.CorrelationId.Value);
    }

    [Fact]
    public async Task Run_ActivityFailure_IsAttributedToTheInnermostNode()
    {
        using var h = new RuntimeHarness();
        var workflow = h.Load(RuntimeHarness.Workflow("""
            { "id": "main", "type": "Test.Sequence", "children": [
              { "id": "outer", "type": "Test.Sequence", "children": [
                { "id": "boom", "type": "Test.Fail", "properties": { "message": "'disk full'" } } ] },
              { "id": "never", "type": "Test.Set", "properties": { "to": "result", "value": "1" } } ] }
            """, """[ { "name": "result", "direction": "Out", "type": "Int" } ]"""));

        var result = await h.Runner.RunTestAsync(workflow, new WorkflowRunRequest());

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Empty(result.Outputs);
        var error = Assert.IsType<ExecutionError>(result.Error);
        Assert.Equal(ExecutionErrorCodes.ActivityFailed, error.Code);
        Assert.Equal("boom", error.NodeId);
        Assert.Equal("Test.Fail", error.ActivityType);
        Assert.Equal("InvalidOperationException", error.ErrorType);
        Assert.Equal("disk full", error.Message);
        Assert.Contains(h.Logs.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error && e.Exception is WorkflowActivityException);
    }

    [Fact]
    public async Task Run_ExpressionFailure_HasExpressionErrorCode()
    {
        using var h = new RuntimeHarness();
        var workflow = h.Load(RuntimeHarness.Workflow(
            """{ "id": "set", "type": "Test.Set", "properties": { "to": "result", "value": "1 / 0" } }""",
            """[ { "name": "result", "direction": "Out", "type": "Int" } ]"""));

        var result = await h.Runner.RunTestAsync(workflow, new WorkflowRunRequest());

        Assert.Equal(ExecutionErrorCodes.ExpressionFailed, result.Error!.Code);
        Assert.Equal("Expression", result.Error.ErrorType);
        Assert.Contains("Division by zero", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_AssignmentTypeMismatch_Fails()
    {
        using var h = new RuntimeHarness();
        var workflow = h.Load(RuntimeHarness.Workflow(
            """{ "id": "set", "type": "Test.Set", "properties": { "to": "result", "value": "'text'" } }""",
            """[ { "name": "result", "direction": "Out", "type": "Int" } ]"""));

        var result = await h.Runner.RunTestAsync(workflow, new WorkflowRunRequest());

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("Cannot store a String value in a Int", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_Cancellation_ReturnsCancelled()
    {
        using var h = new RuntimeHarness();
        using var cts = new CancellationTokenSource();
        var workflow = h.Load(RuntimeHarness.Workflow("""{ "id": "wait", "type": "Test.Wait", "properties": { "milliseconds": 60000 } }"""));

        var run = h.Runner.RunTestAsync(workflow, new WorkflowRunRequest(), cts.Token);
        Assert.False(run.IsCompleted);
        await cts.CancelAsync();
        var result = await run;

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Equal(ExecutionErrorCodes.Cancelled, result.Error!.Code);
    }

    [Fact]
    public async Task Run_AlreadyCancelledToken_ReturnsCancelledWithoutExecuting()
    {
        using var h = new RuntimeHarness();
        var workflow = h.Load(RuntimeHarness.Workflow("""{ "id": "probe", "type": "Test.Probe" }"""));

        var result = await h.Runner.RunTestAsync(workflow, new WorkflowRunRequest(), new CancellationToken(canceled: true));

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.Empty(h.Services.GetService(typeof(ProbeLog)) is ProbeLog log ? log.Seen : []);
    }

    [Fact]
    public async Task Run_Timeout_ReturnsTimedOut_DrivenByFakeClock()
    {
        using var h = new RuntimeHarness();
        var workflow = h.Load(RuntimeHarness.Workflow("""{ "id": "wait", "type": "Test.Wait", "properties": { "milliseconds": 10000 } }"""));

        var run = h.Runner.RunTestAsync(workflow, new WorkflowRunRequest { Timeout = TimeSpan.FromSeconds(1) });
        h.Time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(run.IsCompleted);
        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        var result = await run;

        Assert.Equal(ExecutionStatus.TimedOut, result.Status);
        Assert.Equal(ExecutionErrorCodes.TimedOut, result.Error!.Code);
        Assert.Contains("1000 ms", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_DefaultTimeoutFromOptions_Applies()
    {
        using var h = new RuntimeHarness(configure: o => o.DefaultTimeout = TimeSpan.FromSeconds(2));
        var workflow = h.Load(RuntimeHarness.Workflow("""{ "id": "wait", "type": "Test.Wait", "properties": { "milliseconds": 10000 } }"""));

        var run = h.Runner.RunTestAsync(workflow, new WorkflowRunRequest());
        h.Time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(ExecutionStatus.TimedOut, (await run).Status);
    }

    [Theory]
    [InlineData("unknown", 1, "Unknown argument 'unknown'")]
    [InlineData("result", 1, "Argument 'result' is Out")]
    [InlineData("input", "text", "Cannot store a String value in a Int")]
    public async Task Run_InvalidArguments_FailWithoutExecuting(string name, object value, string expected)
    {
        using var h = new RuntimeHarness();
        var workflow = h.Load(RuntimeHarness.Workflow("""{ "id": "probe", "type": "Test.Probe" }""", _arguments));

        var result = await h.Runner.RunTestAsync(workflow, new WorkflowRunRequest
        {
            Arguments = new Dictionary<string, object?> { ["name"] = "n", [name] = value },
        });

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(ExecutionErrorCodes.InvalidArguments, result.Error!.Code);
        Assert.Contains(expected, result.Error.Message, StringComparison.Ordinal);
        Assert.Empty(((ProbeLog)h.Services.GetService(typeof(ProbeLog))!).Seen);
    }

    [Fact]
    public async Task Run_MissingRequiredArgument_Fails()
    {
        using var h = new RuntimeHarness();
        var workflow = h.Load(RuntimeHarness.Workflow("""{ "id": "noop", "type": "Test.Sequence" }""", _arguments));

        var result = await h.Runner.RunTestAsync(workflow, new WorkflowRunRequest());

        Assert.Contains("Required argument 'name' was not supplied.", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_EachTopLevelRun_GetsItsOwnDiScope_DisposedAfterwards()
    {
        using var h = new RuntimeHarness();
        var workflow = h.Load(RuntimeHarness.Workflow("""
            { "id": "main", "type": "Test.Sequence", "children": [
              { "id": "p1", "type": "Test.Probe" }, { "id": "p2", "type": "Test.Probe" } ] }
            """));

        await h.Runner.RunTestAsync(workflow, new WorkflowRunRequest());
        await h.Runner.RunTestAsync(workflow, new WorkflowRunRequest());

        var seen = ((ProbeLog)h.Services.GetService(typeof(ProbeLog))!).Seen.ToArray();
        Assert.Equal(4, seen.Length);
        Assert.Same(seen[0], seen[1]);
        Assert.Same(seen[2], seen[3]);
        Assert.NotSame(seen[0], seen[2]);
        Assert.All(seen, p => Assert.True(p.Disposed));
    }

    [Fact]
    public async Task Run_EmitsWorkflowAndNodeSpans_WithIdentityTagsAndOutcome()
    {
        var correlation = NewCorrelation();
        using var spans = new SpanCollector(correlation.Value);
        using var h = new RuntimeHarness();
        var workflow = h.Load(RuntimeHarness.Workflow("""
            { "id": "main", "type": "Test.Sequence", "children": [
              { "id": "ok", "type": "Test.Probe" },
              { "id": "bad", "type": "Test.Fail", "properties": { "message": "'nope'" } } ] }
            """));

        var result = await h.Runner.RunTestAsync(workflow, new WorkflowRunRequest { CorrelationId = correlation });

        var byName = spans.Spans.ToDictionary(s => (string)s.GetTagItem(DiagnosticNames.NodeIdKey)! ?? "workflow", s => s);
        var root = Assert.Single(spans.Spans, s => s.OperationName == DiagnosticNames.WorkflowExecuteOperation);
        Assert.Equal(ActivityStatusCode.Error, root.Status);
        Assert.Equal("Failed", root.GetTagItem(DiagnosticNames.OutcomeKey));
        Assert.Equal(result.ExecutionId.ToString(), root.GetTagItem(DiagnosticNames.ExecutionIdKey));
        Assert.Equal("test", root.GetTagItem(DiagnosticNames.WorkflowIdKey));

        var ok = Assert.Single(spans.Spans, s => Equals(s.GetTagItem(DiagnosticNames.NodeIdKey), "ok"));
        Assert.Equal("Test.Probe", ok.OperationName);
        Assert.Equal("Test.Probe", ok.GetTagItem(DiagnosticNames.ActivityTypeKey));
        Assert.Equal(ActivityStatusCode.Ok, ok.Status);

        var bad = Assert.Single(spans.Spans, s => Equals(s.GetTagItem(DiagnosticNames.NodeIdKey), "bad"));
        Assert.Equal(ActivityStatusCode.Error, bad.Status);
        Assert.Contains(bad.Events, e => e.Name == "exception");
        Assert.Equal(root.SpanId, Assert.Single(spans.Spans, s => Equals(s.GetTagItem(DiagnosticNames.NodeIdKey), "main")).ParentSpanId);
        Assert.NotEmpty(byName);
    }

    [Fact]
    public async Task Run_LogsInsideActivities_CarryExecutionWorkflowAndNodeScope()
    {
        using var h = new RuntimeHarness();
        var workflow = h.Load(RuntimeHarness.Workflow("""
            { "id": "main", "type": "Test.Sequence", "children": [ { "id": "say", "type": "Test.Log", "properties": { "message": "'hi'" } } ] }
            """));

        var result = await h.Runner.RunTestAsync(workflow, new WorkflowRunRequest { CorrelationId = new CorrelationId("c-log") });

        var entry = Assert.Single(h.Logs.Entries, e => e.Message == "hi");
        Assert.Equal(result.ExecutionId.ToString(), entry.ScopeValues[DiagnosticNames.ExecutionIdKey]);
        Assert.Equal("c-log", entry.ScopeValues[DiagnosticNames.CorrelationIdKey]);
        Assert.Equal("test", entry.ScopeValues[DiagnosticNames.WorkflowIdKey]);
        Assert.Equal("say", entry.ScopeValues[DiagnosticNames.NodeIdKey]);
    }

    [Fact]
    public async Task Run_CodeConstructedWorkflow_WithUnregisteredActivity_FailsAtThatNode()
    {
        using var h = new RuntimeHarness();
        var workflow = new WorkflowDefinition(
            new WorkflowId("code"), "Code", "1",
            new NodeDefinition(new NodeId("ghost"), new ActivityTypeName("Test.Missing")));

        var result = await h.Runner.RunTestAsync(workflow, new WorkflowRunRequest());

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("ghost", result.Error!.NodeId);
        Assert.Contains("not registered", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_NullArguments_Throw()
    {
        using var h = new RuntimeHarness();
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Runner.RunTestAsync(null!, new WorkflowRunRequest()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Runner.RunTestAsync(h.Load(RuntimeHarness.Workflow("""{ "id": "n", "type": "Test.Sequence" }""")), null!));
    }
}
