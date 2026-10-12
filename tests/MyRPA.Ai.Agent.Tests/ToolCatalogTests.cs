using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MyRPA.Ai.Contracts;

namespace MyRPA.Ai.Agent.Tests;

/// <summary>The agent plugin's tool catalog (ADR-0048 amendment): what the Studio shows and the connection test.</summary>
public sealed class ToolCatalogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string> Stdio(string name, string tools, string approval = "", string? enabled = null)
    {
        var settings = new Dictionary<string, string>
        {
            [$"mcp.{name}.transport"] = "stdio",
            [$"mcp.{name}.command"] = "dotnet",
            [$"mcp.{name}.args"] = JsonSerializer.Serialize(new[] { AgentHost.Metadata("FakeMcpServer") }),
            [$"mcp.{name}.tools"] = tools,
            [$"mcp.{name}.approval"] = approval,
        };
        if (enabled is not null)
        {
            settings[$"mcp.{name}.enabled"] = enabled;
        }

        return settings;
    }

    private static IAgentToolCatalog Catalog(AgentHost host) => Assert.Single(host.Services.GetServices<IAgentToolCatalog>());

    [Fact]
    public async Task Catalog_ListsBuiltInsAndConfiguredServers_WithoutTokenValues()
    {
        var variable = "MYRPA_TEST_MCP_TOKEN_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        Environment.SetEnvironmentVariable(variable, "tok-secret-value");
        var settings = Stdio("local", "echo,add", approval: "add");
        settings["mcp.remote.transport"] = "http";
        settings["mcp.remote.url"] = "https://mcp.example.com/mcp";
        settings["mcp.remote.tokenVariable"] = variable;
        settings["mcp.remote.tools"] = "*";
        settings["mcp.remote.enabled"] = "false";
        await using var host = await AgentHost.StartAsync([], settings);

        var catalog = Catalog(host);

        Assert.Equal(["time_now", "regex_extract", "json_get"], catalog.BuiltIns.Select(b => b.Name));
        Assert.All(catalog.BuiltIns, b => Assert.NotEmpty(b.Description));
        Assert.Equal(["local", "remote"], catalog.Servers.Select(s => s.Name));
        var local = catalog.Servers[0];
        Assert.Equal(("stdio", true), (local.Transport, local.Enabled));
        Assert.Equal(["add", "echo"], local.AllowedTools);
        Assert.Equal(["add"], local.ApprovalTools);
        Assert.StartsWith("dotnet ", local.Endpoint, StringComparison.Ordinal);
        var remote = catalog.Servers[1];
        Assert.Equal(("http", false, "https://mcp.example.com/mcp", variable, true), (remote.Transport, remote.Enabled, remote.Endpoint, remote.TokenVariable, remote.TokenSet));
        Assert.DoesNotContain("tok-secret-value", JsonSerializer.Serialize(catalog.Servers), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_AStdioServer_ListsItsToolsWithWhatTheConfigurationAllows()
    {
        await using var host = await AgentHost.StartAsync([], Stdio("local", "echo,add", approval: "add"));

        var discovery = await Catalog(host).DiscoverAsync("local", Ct);

        Assert.Null(discovery.Error);
        var tools = discovery.Tools.ToDictionary(t => t.Name);
        Assert.Equal(["add", "crash", "echo", "fail", "secret", "slow"], tools.Keys);
        Assert.Equal((true, false), (tools["echo"].Allowed, tools["echo"].RequiresApproval));
        Assert.Equal((true, true), (tools["add"].Allowed, tools["add"].RequiresApproval));
        Assert.False(tools["crash"].Allowed);
        Assert.Equal("Echoes a text.", tools["echo"].Description);
        Assert.Contains("\"text\"", tools["echo"].InputSchema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_AnHttpServer_UsesTheTokenFromTheEnvironment()
    {
        var variable = "MYRPA_TEST_MCP_TOKEN_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        Environment.SetEnvironmentVariable(variable, "tok-123");
        using var server = new FakeHttpMcp(token: "tok-123");
        var settings = new Dictionary<string, string> { ["mcp.remote.transport"] = "http", ["mcp.remote.url"] = server.Url, ["mcp.remote.tokenVariable"] = variable, ["mcp.remote.tools"] = "*" };
        await using var host = await AgentHost.StartAsync([], settings);

        var discovery = await Catalog(host).DiscoverAsync("remote", Ct);

        Assert.Null(discovery.Error);
        Assert.True(Assert.Single(discovery.Tools, t => t.Name == "echo").Allowed);
        Assert.All(server.Received.Where(r => r.Method == "POST"), r => Assert.Equal("Bearer tok-123", r.Authorization));
    }

    [Fact]
    public async Task Discover_ADisabledOrUnknownServer_StartsNothingAndSaysWhy()
    {
        await using var host = await AgentHost.StartAsync([], Stdio("local", "*", enabled: "false"));
        var catalog = Catalog(host);

        var disabled = await catalog.DiscoverAsync("local", Ct);
        var unknown = await catalog.DiscoverAsync("nowhere", Ct);

        Assert.Contains("disabled", disabled.Error, StringComparison.Ordinal);
        Assert.Contains("No MCP server named 'nowhere'", unknown.Error, StringComparison.Ordinal);
        Assert.Empty(disabled.Tools);
    }

    [Fact]
    public async Task Discover_AnUnreachableServer_ReportsTheFailure()
    {
        var settings = new Dictionary<string, string> { ["mcp.remote.transport"] = "http", ["mcp.remote.url"] = $"http://localhost:{FakeHttpMcp.FreePort()}/mcp", ["mcp.remote.tools"] = "*", ["mcp.remote.timeoutMs"] = "3000" };
        await using var host = await AgentHost.StartAsync([], settings);

        var discovery = await Catalog(host).DiscoverAsync("remote", Ct);

        Assert.NotNull(discovery.Error);
        Assert.Empty(discovery.Tools);
    }
}
