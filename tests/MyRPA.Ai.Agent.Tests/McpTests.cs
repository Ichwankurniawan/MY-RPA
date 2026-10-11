using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MyRPA.Core.Execution;
using MyRPA.Plugins;
using MyRPA.Workflow.Execution;
using static MyRPA.Ai.Agent.Tests.Script;

namespace MyRPA.Ai.Agent.Tests;

/// <summary>The MCP client through the agent (ADR-0048): a fake stdio server process and a fake Streamable HTTP server.</summary>
public sealed class McpTests
{
    private static string FakeServer => AgentHost.Metadata("FakeMcpServer");

    private static Dictionary<string, string> Stdio(string tools, string approval = "", string? pidFile = null, string? enabled = null)
    {
        var args = new List<string> { FakeServer };
        if (pidFile is not null)
        {
            args.AddRange(["--pid", pidFile]);
        }

        var settings = new Dictionary<string, string>
        {
            ["mcp.local.transport"] = "stdio",
            ["mcp.local.command"] = "dotnet",
            ["mcp.local.args"] = JsonSerializer.Serialize(args),
            ["mcp.local.tools"] = tools,
            ["mcp.local.approval"] = approval,
        };
        if (enabled is not null)
        {
            settings["mcp.local.enabled"] = enabled;
        }

        return settings;
    }

    private static IReadOnlyList<(string Tool, string Status)> Calls(WorkflowExecutionResult result) =>
        [.. ((IReadOnlyList<object?>)result.Outputs["calls"]!).Cast<IReadOnlyDictionary<string, object?>>().Select(c => ((string)c["tool"]!, (string)c["status"]!))];

    private static async Task AssertExitedAsync(string pidFile)
    {
        var pid = int.Parse(await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken), CultureInfo.InvariantCulture);
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"The MCP server process {pid} is still running after the agent's run ended.");
    }

    [Fact]
    public async Task Stdio_DiscoversAndCallsATool_AndTheServerEndsWithTheRun()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"myrpa-mcp-{Guid.NewGuid():N}.pid");
        await using var host = await AgentHost.StartAsync([Call("local.echo", new { text = "hi" }), Finish(new { said = "hi" })], Stdio("echo", pidFile: pidFile));

        var result = await host.RunAsync(AgentWorkflow("""[ { "mcp": "local", "tool": "echo" } ]"""));

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal([("local.echo", "Succeeded")], Calls(result));
        Assert.Contains("TOOL RESULT of local.echo (Succeeded)", host.SentText(), StringComparison.Ordinal);
        Assert.Contains("Echoes a text.", host.SentText(), StringComparison.Ordinal);
        await AssertExitedAsync(pidFile);
    }

    [Fact]
    public async Task Stdio_ApprovalPermissionsSchemaAndErrors_AreEnforced()
    {
        await using var host = await AgentHost.StartAsync(
            [Call("local.add", new { a = 1, b = 2 }), Call("local.slow", new { }), Call("local.echo", new { text = 5 }), Call("local.fail", new { }), Finish(new { })],
            Stdio("echo,add,fail", approval: "add"));

        var result = await host.RunAsync(AgentWorkflow("""[ { "mcp": "local", "tool": "*" } ]"""));

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal([("local.add", "Denied"), ("local.slow", "Denied"), ("local.echo", "InvalidArguments"), ("local.fail", "Failed")], Calls(result));
        Assert.Contains("needs a person", host.SentText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stdio_AToolTimeout_IsAToolOutcome()
    {
        await using var host = await AgentHost.StartAsync([Call("local.slow", new { }), Finish(new { })], Stdio("slow"));

        var result = await host.RunAsync(AgentWorkflow("""[ { "mcp": "local", "tool": "slow" } ]""", extra: ", \"toolTimeoutMs\": 500"));

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal([("local.slow", "TimedOut")], Calls(result));
    }

    [Fact]
    public async Task Stdio_AServerThatDies_IsAToolFailure_AndTheNextCallReconnects()
    {
        await using var host = await AgentHost.StartAsync([Call("local.crash", new { }), Call("local.echo", new { text = "again" }), Finish(new { })], Stdio("crash,echo"));

        var result = await host.RunAsync(AgentWorkflow("""[ { "mcp": "local", "tool": "*" } ]"""));

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal([("local.crash", "Failed"), ("local.echo", "Succeeded")], Calls(result));
    }

    [Fact]
    public async Task Stdio_SecretsAndInjectedInstructions_InToolOutput_StayData()
    {
        await using var host = await AgentHost.StartAsync([Call("local.secret", new { }), Call("local.crash", new { }), Finish(new { })], Stdio("secret"));

        var result = await host.RunAsync(AgentWorkflow("""[ { "mcp": "local", "tool": "secret" } ]"""));

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal([("local.secret", "Succeeded"), ("local.crash", "Denied")], Calls(result));
        Assert.DoesNotContain("abc123secretvalueXYZ", host.SentText(), StringComparison.Ordinal);
        Assert.Contains("[redacted]", host.SentText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedRuns_UseTheirOwnServerProcess()
    {
        var first = Path.Combine(Path.GetTempPath(), $"myrpa-mcp-{Guid.NewGuid():N}.pid");
        var second = Path.Combine(Path.GetTempPath(), $"myrpa-mcp-{Guid.NewGuid():N}.pid");
        await using var one = await AgentHost.StartAsync([Call("local.echo", new { text = "a" }), Finish(new { })], Stdio("echo", pidFile: first));
        await using var two = await AgentHost.StartAsync([Call("local.echo", new { text = "b" }), Finish(new { })], Stdio("echo", pidFile: second));

        Assert.True((await one.RunAsync(AgentWorkflow("""[ { "mcp": "local", "tool": "echo" } ]"""))).Succeeded);
        Assert.True((await two.RunAsync(AgentWorkflow("""[ { "mcp": "local", "tool": "echo" } ]"""))).Succeeded);

        Assert.NotEqual(await File.ReadAllTextAsync(first, TestContext.Current.CancellationToken), await File.ReadAllTextAsync(second, TestContext.Current.CancellationToken));
        await AssertExitedAsync(first);
        await AssertExitedAsync(second);
    }

    [Fact]
    public async Task ADisabledServer_IsNeverStarted()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"myrpa-mcp-{Guid.NewGuid():N}.pid");
        await using var host = await AgentHost.StartAsync([Finish(new { })], Stdio("echo", pidFile: pidFile, enabled: "false"));

        var result = await host.RunAsync(AgentWorkflow("""[ { "mcp": "local", "tool": "echo" } ]"""));

        Assert.Equal(("AgentToolSourceUnavailable", ExecutionStatus.Failed), (result.Error?.ErrorType, result.Status));
        Assert.Contains("disabled", result.Error!.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(pidFile));
        Assert.Empty(host.Requests());
    }

    [Fact]
    public async Task Http_SendsTheTokenFromTheEnvironment_AndMapsStructuredResults()
    {
        var variable = "MYRPA_TEST_MCP_TOKEN_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        Environment.SetEnvironmentVariable(variable, "tok-123");
        using var server = new FakeHttpMcp(token: "tok-123");
        var settings = new Dictionary<string, string> { ["mcp.remote.transport"] = "http", ["mcp.remote.url"] = server.Url, ["mcp.remote.tokenVariable"] = variable, ["mcp.remote.tools"] = "echo" };
        await using var host = await AgentHost.StartAsync([Call("remote.echo", new { text = "over http" }), Finish(new { })], settings);

        var result = await host.RunAsync(AgentWorkflow("""[ { "mcp": "remote", "tool": "echo" } ]"""));

        Assert.True(result.Succeeded, result.Error?.Message);
        Assert.Equal([("remote.echo", "Succeeded")], Calls(result));
        Assert.Contains("over http", host.SentText(), StringComparison.Ordinal);
        var posts = server.Received.Where(r => r.Method == "POST").ToList();
        Assert.Contains(posts, r => r.Rpc == "initialize");
        Assert.Contains(posts, r => r.Rpc == "tools/call");
        Assert.All(posts, r => Assert.Equal("Bearer tok-123", r.Authorization));
        Assert.DoesNotContain("tok-123", host.SentText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_AMissingToken_AnUnreachableServer_AndARedirect_LeaveTheToolUnavailable()
    {
        using var target = new FakeHttpMcp();
        using var redirecting = new FakeHttpMcp(redirectTo: target.Url);
        Dictionary<string, string> Remote(string url, string? tokenVariable = null)
        {
            var settings = new Dictionary<string, string> { ["mcp.remote.transport"] = "http", ["mcp.remote.url"] = url, ["mcp.remote.tools"] = "echo", ["mcp.remote.timeoutMs"] = "3000" };
            if (tokenVariable is not null)
            {
                settings["mcp.remote.tokenVariable"] = tokenVariable;
            }

            return settings;
        }

        await using var noToken = await AgentHost.StartAsync([Finish(new { })], Remote(target.Url, "MYRPA_TEST_MCP_TOKEN_NOT_SET_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()));
        await using var unreachable = await AgentHost.StartAsync([Finish(new { })], Remote($"http://localhost:{FakeHttpMcp.FreePort()}/mcp"));
        await using var redirected = await AgentHost.StartAsync([Finish(new { })], Remote(redirecting.Url));
        var workflow = AgentWorkflow("""[ { "mcp": "remote", "tool": "echo" } ]""");

        var results = new[] { await noToken.RunAsync(workflow), await unreachable.RunAsync(workflow), await redirected.RunAsync(workflow) };

        Assert.All(results, r => Assert.Equal("AgentToolSourceUnavailable", r.Error?.ErrorType));
        Assert.Contains("needs a token", results[0].Error!.Message, StringComparison.Ordinal);
        Assert.Empty(target.Received);
    }

    [Theory]
    [InlineData("mcp.x.transport", "http", "mcp.x.url", "http://example.com/mcp", "https address")]
    [InlineData("mcp.x.transport", "stdio", "mcp.x.tools", "echo", "'mcp.x.command' is required")]
    [InlineData("mcp.x.transport", "http", "mcp.x.token", "abc", "not allowed")]
    [InlineData("mcp.x.transport", "ftp", "mcp.x.tools", "echo", "must be http or stdio")]
    [InlineData("mcp.x.transport", "http", "mcp.x.url", "https://mcp.example.com/mcp", "must list the tools")]
    public async Task UnsafeOrIncompleteServerSettings_StopThePluginFromLoading(string key1, string value1, string key2, string value2, string reason)
    {
        var options = new PluginHostOptions();
        var source = new PluginSource { Directory = AgentHost.Metadata("AgentPluginDirectory") };
        source.Settings[key1] = value1;
        source.Settings[key2] = value2;
        if (key2 != "mcp.x.tools" && key1 != "mcp.x.tools" && reason != "must list the tools")
        {
            source.Settings["mcp.x.tools"] = "echo";
        }

        if (value1 == "http" && key2 != "mcp.x.url")
        {
            source.Settings["mcp.x.url"] = "https://mcp.example.com/mcp";
        }

        options.Sources.Add(source);
        await using var plugins = await PluginLoader.LoadAsync(options, TestContext.Current.CancellationToken);

        Assert.True(plugins.HasRequiredFailures);
        Assert.Contains(plugins.Diagnostics, d => d.Message.Contains(reason, StringComparison.Ordinal));
    }
}
