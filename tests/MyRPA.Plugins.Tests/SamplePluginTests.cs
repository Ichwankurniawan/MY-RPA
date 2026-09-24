using MyRPA.Core.Execution;

namespace MyRPA.Plugins.Tests;

/// <summary>
/// The shipped sample plugin, end to end: workflow JSON → activity catalog → plugin activity → provider → selector →
/// element → workflow result (PRD Phase 3 definition of done).
/// </summary>
public sealed class SamplePluginTests
{
    [Fact]
    public async Task SampleWorkflow_RunsThroughTheSamplePlugin()
    {
        using var staged = StagedPlugin.Sample();
        await using var host = await PluginTestHost.StartAsync(staged.Source());

        var result = await host.RunAsync(File.ReadAllText(PluginPaths.SampleWorkflow), new Dictionary<string, object?> { ["customer"] = "Grace" });

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal("Hello, Grace", result.Outputs["echoed"]);
        Assert.Equal("London", result.Outputs["city"]);
        // Demo.GetField fails with a classified error that Core.TryCatch exposes as err.errorType.
        Assert.Equal("ElementNotFound", result.Outputs["missingField"]);
    }

    [Fact]
    public async Task SamplePlugin_ReceivesItsSettings()
    {
        using var staged = StagedPlugin.Sample();
        await using var host = await PluginTestHost.StartAsync(staged.Source(settings: ("echoPrefix", ">> ")));

        var result = await host.RunAsync(File.ReadAllText(PluginPaths.SampleWorkflow));

        Assert.Equal(">> Hello, Ada", result.Outputs["echoed"]);
    }

    [Fact]
    public async Task SamplePlugin_RejectsInvalidSettings()
    {
        using var staged = StagedPlugin.Sample();
        await using var set = await PluginTestHost.LoadAsync(staged.Source(settings: ("echoPrefix", new string('x', 40))));

        Assert.Equal(PluginDiagnosticCodes.InitializationFailed, Assert.Single(set.Diagnostics).Code);
    }

    [Fact]
    public void SamplePluginDirectory_ContainsNoHostAssemblies()
    {
        // Contract assemblies come from the host; a clean plugin directory ships only its own files.
        var files = Directory.EnumerateFiles(PluginPaths.Sample).Select(Path.GetFileName).ToList();

        Assert.Contains("myrpa-plugin.json", files);
        Assert.Contains("MyRPA.Samples.DemoPlugin.dll", files);
        Assert.DoesNotContain(files, f => f!.StartsWith("MyRPA.Core", StringComparison.Ordinal) || f.StartsWith("MyRPA.Workflow", StringComparison.Ordinal)
            || f.StartsWith("MyRPA.Sdk", StringComparison.Ordinal) || f.StartsWith("Microsoft.Extensions", StringComparison.Ordinal));
    }
}
