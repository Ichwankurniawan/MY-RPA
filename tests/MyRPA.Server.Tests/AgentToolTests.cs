using System.Net;
using System.Reflection;
using System.Text.Json;

namespace MyRPA.Server.Tests;

/// <summary><c>/api/agent-tools</c> (ADR-0048 amendment): the agent plugin's built-ins and MCP servers, and the connection test.</summary>
public sealed class AgentToolTests
{
    private static string Metadata(string key) => Path.GetFullPath(
        typeof(AgentToolTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!);

    private static async Task<ServerHarness> StartWithAgentAsync()
    {
        var folder = Directory.CreateTempSubdirectory("myrpa-agent-tools-").FullName;
        var settings = new Dictionary<string, string>
        {
            ["mcp.local.transport"] = "stdio",
            ["mcp.local.command"] = "dotnet",
            ["mcp.local.args"] = JsonSerializer.Serialize(new[] { Metadata("FakeMcpServer") }),
            ["mcp.local.tools"] = "echo,add",
            ["mcp.local.approval"] = "add",
            ["mcp.off.transport"] = "http",
            ["mcp.off.url"] = "https://mcp.example.com/mcp",
            ["mcp.off.tools"] = "*",
            ["mcp.off.enabled"] = "false",
        };
        var config = Path.Combine(folder, "plugins.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new { pluginConfigVersion = "1.0", plugins = new[] { new { directory = Metadata("AgentPluginDirectory"), settings } } }));
        return await ServerHarness.StartAsync(o => o with { PluginConfiguration = config });
    }

    [Fact]
    public async Task List_WithoutTheAgentPlugin_SaysWhy()
    {
        await using var h = await ServerHarness.StartAsync();

        var json = JsonDocument.Parse(await h.Client.GetStringAsync("/api/agent-tools", TestContext.Current.CancellationToken)).RootElement;
        using var discover = await h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/agent-tools/mcp/local/discover"));

        Assert.False(json.GetProperty("available").GetBoolean());
        Assert.Contains("MyRPA.Ai.Agent", json.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, discover.StatusCode);
    }

    [Fact]
    public async Task List_ShowsBuiltInsAndServers_AndDiscoveryListsTheServersTools()
    {
        await using var h = await StartWithAgentAsync();

        var json = JsonDocument.Parse(await h.Client.GetStringAsync("/api/agent-tools", TestContext.Current.CancellationToken)).RootElement;
        Assert.True(json.GetProperty("available").GetBoolean());
        Assert.Equal(3, json.GetProperty("builtIns").GetArrayLength());
        var servers = json.GetProperty("servers").EnumerateArray().ToList();
        Assert.Equal(["local", "off"], servers.Select(s => s.GetProperty("name").GetString()));
        Assert.Equal("stdio", servers[0].GetProperty("transport").GetString());
        Assert.False(servers[1].GetProperty("enabled").GetBoolean());

        using var response = await h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/agent-tools/mcp/local/discover"));
        var discovery = await ServerHarness.JsonAsync(response);
        Assert.True(discovery.GetProperty("ok").GetBoolean(), discovery.ToString());
        var echo = discovery.GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "echo");
        Assert.True(echo.GetProperty("allowed").GetBoolean());
        Assert.Equal("object", echo.GetProperty("inputSchema").GetProperty("type").GetString());

        using var disabled = await h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/agent-tools/mcp/off/discover"));
        Assert.Contains("disabled", (await ServerHarness.JsonAsync(disabled)).GetProperty("error").GetString(), StringComparison.Ordinal);
        using var unknown = await h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/agent-tools/mcp/nowhere/discover"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Discover_WithoutTheAntiForgeryHeader_IsRefused()
    {
        await using var h = await StartWithAgentAsync();

        using var response = await h.Client.PostAsync("/api/agent-tools/mcp/local/discover", null, TestContext.Current.CancellationToken);

        Assert.False(response.IsSuccessStatusCode);
    }
}
