using System.Text.Json;
using MyRPA.Cli;

namespace MyRPA.Integration.Tests;

/// <summary><c>myrpa validate</c> and <c>myrpa run</c> end to end (files → loader → engine → result).</summary>
public sealed class CliWorkflowTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static JsonElement Json(string stdout) => JsonDocument.Parse(stdout).RootElement.Clone();

    private string Greeter() => _workspace.Write("greeter.json", TempWorkspace.Workflow(
        """
        { "id": "main", "type": "Core.Sequence", "children": [
          { "id": "build", "type": "Core.Assign", "properties": { "to": "greeting", "value": "'Hello, ' + name + ' x' + times" } },
          { "id": "log", "type": "Core.Log", "properties": { "message": "greeting" } } ] }
        """,
        """
        [ { "name": "name", "direction": "In", "type": "String", "required": true },
          { "name": "times", "direction": "In", "type": "Int", "default": 1 },
          { "name": "greeting", "direction": "Out", "type": "String" } ]
        """,
        id: "greeter"));

    [Fact]
    public async Task Validate_ValidWorkflow_Succeeds()
    {
        var result = await Cli.RunAsync("validate", Greeter());

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("Valid: greeter 'Test' version 1.0.0 (schema 1.0)", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_InvalidWorkflow_ReportsAllErrors_WithExitCode3()
    {
        var path = _workspace.Write("bad.json", TempWorkspace.Workflow("""
            { "id": "main", "type": "Core.Sequence", "children": [
              { "id": "a", "type": "Core.Assign", "properties": { "to": "missing", "value": "1 +" } },
              { "id": "a", "type": "Core.Unknown" } ] }
            """));

        var result = await Cli.RunAsync("validate", path);

        Assert.Equal(CliExitCodes.InvalidWorkflow, result.ExitCode);
        Assert.Contains("error MYRPA1045 $.root.children[0].properties.to", result.Out, StringComparison.Ordinal);
        Assert.Contains("error MYRPA1043", result.Out, StringComparison.Ordinal);
        Assert.Contains("error MYRPA1031", result.Out, StringComparison.Ordinal);
        Assert.Contains("error MYRPA1033", result.Out, StringComparison.Ordinal);
        Assert.Contains("Invalid: 4 error(s).", result.Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("validate a.json b.json")]
    [InlineData("run")]
    [InlineData("run wf.json --timeout -1")]
    [InlineData("run wf.json --arg novalue")]
    [InlineData("run wf.json --unknown")]
    public async Task UsageErrors_ReturnExitCode2(string commandLine)
    {
        var result = await Cli.RunAsync(commandLine.Split(' '));

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains("Usage: myrpa", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingFile_ReturnsExitCode2()
    {
        var path = Path.Combine(_workspace.Root, "nope.json");

        Assert.Equal(CliExitCodes.Usage, (await Cli.RunAsync("validate", path)).ExitCode);
        var run = await Cli.RunAsync("run", path);
        Assert.Equal(CliExitCodes.Usage, run.ExitCode);
        Assert.Contains("does not exist", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_Success_PrintsJsonResult_WithOutputsAndIds()
    {
        var result = await Cli.RunAsync("run", Greeter(), "--arg", "name=Ada", "--arg", "times=3", "--correlation-id", "ticket-42");

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        var json = Json(result.Out);
        Assert.Equal("Succeeded", json.GetProperty("status").GetString());
        Assert.Equal("greeter", json.GetProperty("workflowId").GetString());
        Assert.Equal("ticket-42", json.GetProperty("correlationId").GetString());
        Assert.Matches("^[0-9a-f]{32}$", json.GetProperty("executionId").GetString()!);
        Assert.Equal("Hello, Ada x3", json.GetProperty("outputs").GetProperty("greeting").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task Run_InvalidWorkflow_IsNotExecuted_ExitCode3()
    {
        var path = _workspace.Write("bad.json", TempWorkspace.Workflow("""{ "id": "x", "type": "Core.Throw" }"""));

        var result = await Cli.RunAsync("run", path);

        Assert.Equal(CliExitCodes.InvalidWorkflow, result.ExitCode);
        Assert.Empty(result.Out);
        Assert.Contains("error MYRPA1040", result.Error, StringComparison.Ordinal);
        Assert.Contains("The workflow was not executed.", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--arg", "unknown=1", "has no input argument 'unknown'")]
    [InlineData("--arg", "greeting=1", "has no input argument 'greeting'")]
    [InlineData("--arg", "times=many", "'many' is not a valid Int value")]
    public async Task Run_InvalidArguments_ExitCode2(string option, string value, string expected)
    {
        var result = await Cli.RunAsync("run", Greeter(), "--arg", "name=x", option, value);

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_MissingRequiredArgument_FailsWithInvalidArgumentsError()
    {
        var result = await Cli.RunAsync("run", Greeter());

        Assert.Equal(CliExitCodes.Failure, result.ExitCode);
        Assert.Equal("MYRPA2004", Json(result.Out).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Run_WorkflowFailure_ExitCode1_WithErrorDetails()
    {
        var path = _workspace.Write("fail.json", TempWorkspace.Workflow("""{ "id": "stop", "type": "Core.Throw", "properties": { "message": "'Stop here'" } }"""));

        var result = await Cli.RunAsync("run", path);

        Assert.Equal(CliExitCodes.Failure, result.ExitCode);
        var error = Json(result.Out).GetProperty("error");
        Assert.Equal("MYRPA2002", error.GetProperty("code").GetString());
        Assert.Equal("Stop here", error.GetProperty("message").GetString());
        Assert.Equal("stop", error.GetProperty("nodeId").GetString());
    }

    [Fact]
    public async Task Run_Timeout_ExitCode4()
    {
        var path = _workspace.Write("slow.json", TempWorkspace.Workflow("""{ "id": "wait", "type": "Core.Delay", "properties": { "milliseconds": 600000 } }"""));

        var result = await Cli.RunAsync("run", path, "--timeout", "0.2");

        Assert.Equal(CliExitCodes.TimedOut, result.ExitCode);
        Assert.Equal("TimedOut", Json(result.Out).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Run_CancelledWhileRunning_ExitCode130()
    {
        var path = _workspace.Write("slow.json", TempWorkspace.Workflow("""{ "id": "wait", "type": "Core.Delay", "properties": { "milliseconds": 600000 } }"""));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        var result = await Cli.RunAsync(cts.Token, "run", path);

        Assert.Equal(CliExitCodes.Cancelled, result.ExitCode);
        Assert.Equal("Cancelled", Json(result.Out).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Run_InvokeWorkflow_ResolvesRelativeFiles()
    {
        _workspace.Write("lib/double.json", TempWorkspace.Workflow(
            """{ "id": "d", "type": "Core.Assign", "properties": { "to": "result", "value": "value * 2" } }""",
            """[ { "name": "value", "direction": "In", "type": "Int", "required": true }, { "name": "result", "direction": "Out", "type": "Int" } ]""",
            id: "double"));
        var main = _workspace.Write("main.json", TempWorkspace.Workflow(
            """{ "id": "call", "type": "Core.InvokeWorkflow", "properties": { "workflow": "lib/double.json", "arguments": { "value": 21 }, "outputs": { "result": "answer" } } }""",
            """[ { "name": "answer", "direction": "Out", "type": "Int" } ]"""));

        var result = await Cli.RunAsync("run", main);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(42, Json(result.Out).GetProperty("outputs").GetProperty("answer").GetInt32());
    }

    [Fact]
    public async Task Run_InvokeWorkflow_CannotEscapeTheWorkflowRoot()
    {
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "myrpa-escape-target.json"), TempWorkspace.Workflow("""{ "id": "x", "type": "Core.Sequence" }"""));
        var main = _workspace.Write("main.json", TempWorkspace.Workflow(
            """{ "id": "call", "type": "Core.InvokeWorkflow", "properties": { "workflow": "../myrpa-escape-target.json" } }"""));

        var result = await Cli.RunAsync("run", main);

        Assert.Equal(CliExitCodes.Failure, result.ExitCode);
        Assert.Contains("outside the workflow root", Json(result.Out).GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    public static TheoryData<string> Samples() =>
        [.. Directory.EnumerateFiles(RepositoryPaths.Samples, "*.json", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith("invalid.json", StringComparison.Ordinal))
            // Plugin samples need their plugin (see CliPluginTests); plugin build output also contains *.json files.
            .Where(p => !Path.GetRelativePath(RepositoryPaths.Samples, p).StartsWith("plugins", StringComparison.Ordinal))
            .Select(p => Path.GetRelativePath(RepositoryPaths.Samples, p))];

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task ShippedSamples_AreValid(string sample)
    {
        var result = await Cli.RunAsync("validate", Path.Combine(RepositoryPaths.Samples, sample));
        Assert.Equal(CliExitCodes.Success, result.ExitCode);
    }

    [Theory]
    [InlineData("hello-world.json", "greeting", "Hello, World!")]
    [InlineData("control-flow.json", "handledError", "Out of stock (node fail-on-purpose)")]
    public async Task ShippedSamples_Run(string sample, string output, string expected)
    {
        var result = await Cli.RunAsync("run", Path.Combine(RepositoryPaths.Samples, sample));

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(expected, Json(result.Out).GetProperty("outputs").GetProperty(output).GetString());
    }

    [Fact]
    public async Task ShippedInvalidSample_ReportsEightErrors()
    {
        var result = await Cli.RunAsync("validate", Path.Combine(RepositoryPaths.Samples, "invalid.json"));

        Assert.Equal(CliExitCodes.InvalidWorkflow, result.ExitCode);
        Assert.Contains("Invalid: 8 error(s).", result.Out, StringComparison.Ordinal);
    }
}
