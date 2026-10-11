using MyRPA.Core.Activities;
using MyRPA.Core.Execution;
using static MyRPA.Ai.Agent.Tests.Script;

namespace MyRPA.Ai.Agent.Tests;

/// <summary>Catalog discovery, configuration errors and saved-workflow compatibility (ADR-0048).</summary>
public sealed class ConfigurationTests
{
    [Fact]
    public async Task AiAgent_IsAnOrdinaryCatalogActivity()
    {
        await using var host = await AgentHost.StartAsync([Finish(new { })]);

        var load = host.Loader.Load(AgentWorkflow());

        Assert.True(load.IsValid);
        Assert.True(host.Catalog.TryGet(new ActivityTypeName("AI.Agent"), out var descriptor));
        Assert.Equal(["instructions", "task", "tools", "outputSchema", "maxIterations", "maxToolCalls", "timeoutMs", "toolTimeoutMs", "maxTokens", "result", "toolCalls"], descriptor.Properties.Select(p => p.Name));
        Assert.True(descriptor.Properties.Single(p => p.Name == "instructions").IsMultiline);
        Assert.Equal(ActivitySideEffects.Network, descriptor.SideEffects);
    }

    [Theory]
    [InlineData("not json", "", "'tools' is not JSON")]
    [InlineData("""[ { "builtin": "rm" } ]""", "", "There is no built-in tool 'rm'")]
    [InlineData("""[ { "mcp": "nowhere", "tool": "x" } ]""", "", "There is no MCP server 'nowhere'")]
    [InlineData("""[ { "builtin": "time_now" }, { "builtin": "time_now" } ]""", "", "listed twice")]
    [InlineData("""[ { "workflow": "t.json", "name": "t", "input": { "a": "Money" } } ]""", "", "unknown type 'Money'")]
    [InlineData("", "[1]", "'outputSchema' must be a JSON schema object")]
    public async Task BadConfiguration_FailsAsInvalidInput_BeforeTheModelIsAsked(string tools, string schema, string message)
    {
        await using var host = await AgentHost.StartAsync([Finish(new { })]);

        var result = await host.RunAsync(AgentWorkflow(tools, schema));

        Assert.True(result.Status == ExecutionStatus.Failed && result.Error!.ErrorType == "InvalidInput", $"{result.Error?.ErrorType} {result.Error?.Message}");
        Assert.Contains(message, result.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(host.Requests());
    }

    [Fact]
    public async Task WithoutAProvider_OrWithAnUnavailableOne_TheAgentIsNotConfigured()
    {
        await using var none = await AgentHost.StartAsync([], withProvider: false);
        await using var unavailable = await AgentHost.StartAsync([Finish(new { })], unavailable: "Set OPENAI_API_KEY.");

        var first = await none.RunAsync(AgentWorkflow());
        var second = await unavailable.RunAsync(AgentWorkflow());

        Assert.Equal("AgentNotConfigured", first.Error!.ErrorType);
        Assert.Equal("AgentNotConfigured", second.Error!.ErrorType);
        Assert.Contains("Set OPENAI_API_KEY.", second.Error.Message, StringComparison.Ordinal);
        Assert.Empty(unavailable.Requests());
    }

    [Fact]
    public async Task ExistingWorkflows_AreUnchanged_ByThePlugin()
    {
        await using var host = await AgentHost.StartAsync([Finish(new { })]);

        var result = await host.RunAsync("""{ "schemaVersion": "1.0", "id": "plain", "name": "Plain", "version": "1", "root": { "id": "say", "type": "Core.Log", "properties": { "message": "'hello'" } } }""");

        Assert.True(result.Succeeded);
        Assert.Contains("hello", host.Log.Messages);
        Assert.Empty(host.Requests());
    }
}
