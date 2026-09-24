using System.Reflection;
using System.Text.Json;
using MyRPA.Cli;

namespace MyRPA.Integration.Tests;

/// <summary><c>--plugin</c> and <c>myrpa plugins</c> with the shipped sample plugin.</summary>
public sealed class CliPluginTests
{
    private static string SamplePlugin { get; } = Path.GetFullPath(
        typeof(CliPluginTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "SamplePluginDirectory").Value!);

    private static string SampleWorkflow => Path.Combine(RepositoryPaths.Samples, "plugins", "demo-plugin.json");

    private static JsonElement Outputs(string stdout) => JsonDocument.Parse(stdout).RootElement.GetProperty("outputs");

    [Fact]
    public async Task Run_WithPlugin_ExecutesPluginActivities()
    {
        var result = await Cli.RunAsync("--plugin", SamplePlugin, "run", SampleWorkflow, "--arg", "customer=Lin");

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        var outputs = Outputs(result.Out);
        Assert.Equal("Hello, Lin", outputs.GetProperty("echoed").GetString());
        Assert.Equal("London", outputs.GetProperty("city").GetString());
        Assert.Equal("ElementNotFound", outputs.GetProperty("missingField").GetString());
    }

    [Fact]
    public async Task Validate_PluginSample_NeedsThePlugin()
    {
        Assert.Equal(CliExitCodes.Success, (await Cli.RunAsync("--plugin", SamplePlugin, "validate", SampleWorkflow)).ExitCode);

        var without = await Cli.RunAsync("validate", SampleWorkflow);
        Assert.Equal(CliExitCodes.InvalidWorkflow, without.ExitCode);
        Assert.Contains("'Demo.Echo' is not registered", without.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plugins_ListsTheLoadedPlugin()
    {
        var result = await Cli.RunAsync("--plugin", SamplePlugin, "plugins");

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("MyRPA.Samples.Demo 1.0.0", result.Out, StringComparison.Ordinal);
        Assert.Contains("Activities:   Demo.Echo, Demo.GetField", result.Out, StringComparison.Ordinal);
        Assert.Matches("SHA-256: +[0-9a-f]{64}", result.Out);
    }

    [Fact]
    public async Task Info_ShowsThePluginSourceOfActivities()
    {
        var result = await Cli.RunAsync("--plugin", SamplePlugin, "info");

        Assert.Contains("Registered activity types: 14", result.Out, StringComparison.Ordinal);
        Assert.Contains("[plugin MyRPA.Samples.Demo]", result.Out, StringComparison.Ordinal);
        Assert.Contains("Plugins: 1", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PluginOption_WithoutDirectory_IsAUsageError()
    {
        var result = await Cli.RunAsync("info", "--plugin");

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Contains("--plugin needs a plugin directory", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrokenPlugin_StopsTheCommand_WithPluginFailure()
    {
        // The repository samples folder is not a plugin (no manifest): nothing runs.
        var result = await Cli.RunAsync("--plugin", RepositoryPaths.Samples, "run", Path.Combine(RepositoryPaths.Samples, "hello-world.json"));

        Assert.Equal(CliExitCodes.PluginFailure, result.ExitCode);
        Assert.Contains("MYRPA3002", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Out);
    }

    [Fact]
    public async Task Process_RunWithPlugin_WritesOnlyTheResultToStdout()
    {
        using var workspace = new TempWorkspace();

        var result = await CliProcess.RunAsync(workspace.Root, "--plugin", SamplePlugin, "run", SampleWorkflow);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal("Hello, Ada", Outputs(result.Out).GetProperty("echoed").GetString());
    }
}
