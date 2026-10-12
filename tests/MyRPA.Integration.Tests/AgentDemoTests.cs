using System.Reflection;
using System.Text.Json;
using MyRPA.Cli;

namespace MyRPA.Integration.Tests;

/// <summary>The ADR-0048 acceptance scenario through the CLI: prepare, agent with a workflow tool, validated output, decision.</summary>
public sealed class AgentDemoTests
{
    private static string Directory(string key) => Path.GetFullPath(
        typeof(AgentDemoTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!);

    [Fact]
    public async Task AgentDemo_ChoosesAnAuthorizedTool_ReturnsValidatedOutput_AndTheWorkflowDecides()
    {
        using var workspace = new TempWorkspace();
        var answers = Path.Combine(RepositoryPaths.Samples, "plugins", "agent-demo", "answers.json");
        var config = workspace.Write("agent.json", $$"""
            { "pluginConfigVersion": "1.0", "plugins": [
              { "directory": {{JsonSerializer.Serialize(Directory("ScriptedAiDirectory"))}}, "settings": { "answers": {{JsonSerializer.Serialize(answers)}} } },
              { "directory": {{JsonSerializer.Serialize(Directory("AgentPluginDirectory"))}} } ] }
            """);

        var result = await Cli.RunAsync("--plugin-config", config, "run", Path.Combine(RepositoryPaths.Samples, "plugins", "agent-demo.json"));

        Assert.True(result.ExitCode == CliExitCodes.Success, result.Out + result.Error);
        var outputs = JsonDocument.Parse(result.Out).RootElement.GetProperty("outputs");
        Assert.Equal("approve", outputs.GetProperty("decision").GetString());
        Assert.Equal(500m, outputs.GetProperty("extracted").GetProperty("creditLimit").GetDecimal());
        var call = Assert.Single(outputs.GetProperty("toolCalls").EnumerateArray());
        Assert.Equal(("credit_limit", "Succeeded"), (call.GetProperty("tool").GetString(), call.GetProperty("status").GetString()));
    }
}
