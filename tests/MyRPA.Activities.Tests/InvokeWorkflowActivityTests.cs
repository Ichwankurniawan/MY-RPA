using MyRPA.Core.Execution;
using MyRPA.Workflow.Execution;

namespace MyRPA.Activities.Tests;

public sealed class InvokeWorkflowActivityTests
{
    private static readonly Dictionary<string, string> _workflows = new()
    {
        ["greet.json"] = ActivityHarness.Workflow(
            """{ "id": "g", "type": "Core.Assign", "properties": { "to": "greeting", "value": "'Hi ' + who" } }""",
            """[ { "name": "who", "direction": "In", "type": "String", "required": true }, { "name": "greeting", "direction": "Out", "type": "String" } ]""",
            id: "greet"),
        ["fail.json"] = ActivityHarness.Workflow("""{ "id": "x", "type": "Core.Throw", "properties": { "message": "'child says no'" } }""", id: "fail"),
        ["slow.json"] = ActivityHarness.Workflow("""{ "id": "d", "type": "Core.Delay", "properties": { "milliseconds": 60000 } }""", id: "slow"),
    };

    private static string Parent(string properties) => ActivityHarness.Workflow(
        $$"""{ "id": "call", "type": "Core.InvokeWorkflow", "properties": {{properties}} }""",
        """[ { "name": "result", "direction": "Out", "type": "String" } ]""",
        id: "parent");

    [Fact]
    public async Task InvokeWorkflow_MapsArgumentsAndOutputs()
    {
        using var h = new ActivityHarness(_workflows);
        var result = await h.RunAsync(Parent("""{ "workflow": "greet.json", "arguments": { "who": "'Ada'" }, "outputs": { "greeting": "result" } }"""));

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal("Hi Ada", result.Outputs["result"]);
    }

    [Fact]
    public async Task InvokeWorkflow_ChildFailure_ReportsChildNode()
    {
        using var h = new ActivityHarness(_workflows);
        var result = await h.RunAsync(Parent("""{ "workflow": "fail.json" }"""));

        Assert.Equal(ExecutionErrorCodes.InvokedWorkflowFailed, result.Error!.Code);
        Assert.Equal("call", result.Error.NodeId);
        Assert.Equal("Invoked workflow 'fail' Failed at node 'x': child says no", result.Error.Message);
    }

    [Fact]
    public async Task InvokeWorkflow_Timeout_FailsTheNode()
    {
        using var h = new ActivityHarness(_workflows);
        var run = h.RunAsync(Parent("""{ "workflow": "slow.json", "timeoutMilliseconds": 100 }"""));
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        var result = await run;

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Contains("TimedOut", result.Error!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "workflow": "greet.json", "arguments": { "who": "'x'" }, "outputs": { "nope": "result" } }""", "has no output argument 'nope'")]
    [InlineData("""{ "workflow": "greet.json", "arguments": { "who": "'x'" }, "timeoutMilliseconds": 0 }""", "must be positive")]
    [InlineData("""{ "workflow": "missing.json" }""", "not found")]
    public async Task InvokeWorkflow_Misuse_FailsTheNode(string properties, string expected)
    {
        using var h = new ActivityHarness(_workflows);
        var result = await h.RunAsync(Parent(properties));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal("call", result.Error!.NodeId);
        Assert.Contains(expected, result.Error.Message, StringComparison.Ordinal);
    }
}
