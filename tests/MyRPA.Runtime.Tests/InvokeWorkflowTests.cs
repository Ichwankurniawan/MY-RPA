using MyRPA.Core.Diagnostics;
using MyRPA.Core.Execution;
using MyRPA.Core.Identifiers;
using MyRPA.Workflow.Execution;

namespace MyRPA.Runtime.Tests;

/// <summary>Nested executions through <see cref="IActivityContext.InvokeWorkflowAsync"/> (ADR-0012).</summary>
public sealed class InvokeWorkflowTests
{
    private static readonly string _child = RuntimeHarness.Workflow(
        """{ "id": "double", "type": "Test.Set", "properties": { "to": "doubled", "value": "x * 2" } }""",
        """[ { "name": "x", "direction": "In", "type": "Int", "required": true }, { "name": "doubled", "direction": "Out", "type": "Int" } ]""",
        id: "child");

    private static readonly string _failingChild = RuntimeHarness.Workflow(
        """{ "id": "explode", "type": "Test.Fail", "properties": { "message": "'child broke'" } }""", id: "failing");

    private static readonly string _slowChild = RuntimeHarness.Workflow(
        """{ "id": "slow", "type": "Test.Wait", "properties": { "milliseconds": 60000 } }""", id: "slow");

    private static readonly string _recursive = RuntimeHarness.Workflow(
        """{ "id": "again", "type": "Test.Invoke", "properties": { "workflow": "self" } }""", id: "self");

    private static string Parent(string invokeProperties) => RuntimeHarness.Workflow(
        $$"""{ "id": "call", "type": "Test.Invoke", "properties": {{invokeProperties}} }""",
        """[ { "name": "answer", "direction": "Out", "type": "Int" } ]""",
        id: "parent");

    private static RuntimeHarness Harness(Action<WorkflowRuntimeOptions>? configure = null, bool registerResolver = true) =>
        new(new Dictionary<string, string>
        {
            ["child"] = _child,
            ["failing"] = _failingChild,
            ["slow"] = _slowChild,
            ["self"] = _recursive,
        }, configure, registerResolver);

    [Fact]
    public async Task Invoke_MapsArgumentsAndOutputs_AndLinksIdentity()
    {
        var correlation = new CorrelationId("invoke-" + Guid.NewGuid().ToString("N"));
        using var spans = new SpanCollector(correlation.Value);
        using var h = Harness();
        var parent = h.Load(Parent("""{ "workflow": "child", "arguments": { "x": 21 }, "outputs": { "doubled": "answer" } }"""));

        var result = await h.Runner.RunTestAsync(parent, new WorkflowRunRequest { Location = "parent", CorrelationId = correlation });

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(42L, result.Outputs["answer"]);

        var childRoot = Assert.Single(spans.Spans, s => s.OperationName == DiagnosticNames.WorkflowExecuteOperation && Equals(s.GetTagItem(DiagnosticNames.WorkflowIdKey), "child"));
        Assert.Equal(result.ExecutionId.ToString(), childRoot.GetTagItem(DiagnosticNames.ParentExecutionIdKey));
        Assert.NotEqual(result.ExecutionId.ToString(), childRoot.GetTagItem(DiagnosticNames.ExecutionIdKey));
        Assert.Equal(correlation.Value, childRoot.GetTagItem(DiagnosticNames.CorrelationIdKey));
        var invokeNode = Assert.Single(spans.Spans, s => Equals(s.GetTagItem(DiagnosticNames.NodeIdKey), "call"));
        Assert.Equal(invokeNode.SpanId, childRoot.ParentSpanId);
    }

    [Fact]
    public async Task Invoke_ChildFailure_FailsTheInvokingNode()
    {
        using var h = Harness();
        var parent = h.Load(Parent("""{ "workflow": "failing" }"""));

        var result = await h.Runner.RunTestAsync(parent, new WorkflowRunRequest { Location = "parent" });

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("call", result.Error!.NodeId);
        Assert.Equal(ExecutionErrorCodes.InvokedWorkflowFailed, result.Error.Code);
        Assert.Contains("child broke", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_ChildArgumentsAreValidated()
    {
        using var h = Harness();
        var parent = h.Load(Parent("""{ "workflow": "child" }"""));

        var result = await h.Runner.RunTestAsync(parent, new WorkflowRunRequest { Location = "parent" });

        Assert.Contains("Required argument 'x'", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_ChildTimeout_FailsTheInvokingNode_NotTheWholeRunAsTimedOut()
    {
        using var h = Harness();
        var parent = h.Load(Parent("""{ "workflow": "slow", "timeoutMilliseconds": 500 }"""));

        var run = h.Runner.RunTestAsync(parent, new WorkflowRunRequest { Location = "parent" });
        h.Time.Advance(TimeSpan.FromMilliseconds(500));
        var result = await run;

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("TimedOut", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoke_ParentTimeout_WhileChildRuns_IsTimedOut()
    {
        using var h = Harness();
        var parent = h.Load(Parent("""{ "workflow": "slow" }"""));

        var run = h.Runner.RunTestAsync(parent, new WorkflowRunRequest { Location = "parent", Timeout = TimeSpan.FromSeconds(1) });
        h.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(ExecutionStatus.TimedOut, (await run).Status);
    }

    [Fact]
    public async Task Invoke_ParentCancellation_PropagatesAsCancelled()
    {
        using var h = Harness();
        using var cts = new CancellationTokenSource();
        var parent = h.Load(Parent("""{ "workflow": "slow" }"""));

        var run = h.Runner.RunTestAsync(parent, new WorkflowRunRequest { Location = "parent" }, cts.Token);
        await cts.CancelAsync();

        Assert.Equal(ExecutionStatus.Cancelled, (await run).Status);
    }

    [Fact]
    public async Task Invoke_Recursion_IsLimitedByMaxInvocationDepth()
    {
        using var h = Harness(o => o.MaxInvocationDepth = 3);
        var parent = h.Load(Parent("""{ "workflow": "self" }"""));

        var result = await h.Runner.RunTestAsync(parent, new WorkflowRunRequest { Location = "parent" });

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("maximum invocation depth of 3", result.Error!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, null, "no location")]
    [InlineData(true, "parent", "not found")]
    [InlineData(false, "parent", "no workflow resolver")]
    public async Task Invoke_ResolutionProblems_FailTheNode(bool registerResolver, string? location, string expected)
    {
        using var h = Harness(registerResolver: registerResolver);
        var parent = h.Load(Parent("""{ "workflow": "missing" }"""));

        var result = await h.Runner.RunTestAsync(parent, new WorkflowRunRequest { Location = location });

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains(expected, result.Error!.Message, StringComparison.Ordinal);
    }
}
