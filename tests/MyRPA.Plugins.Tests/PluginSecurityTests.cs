using Microsoft.Extensions.DependencyInjection;
using MyRPA.Workflow.Validation;

namespace MyRPA.Plugins.Tests;

/// <summary>What the plugin system must never do (ADR-0015).</summary>
public sealed class PluginSecurityTests
{
    [Fact]
    public async Task WorkflowData_NeverCausesPluginLoading()
    {
        // A workflow naming a plugin activity (or a CLR type) is just invalid when no such plugin is configured.
        await using var host = PluginTestHost.Start(await PluginTestHost.LoadAsync());
        var loader = host.Services.GetRequiredService<WorkflowLoader>();

        var result = loader.Load(PluginTestHost.Workflow("""{ "id": "x", "type": "Fixture.Journal", "properties": { "to": "r" } }"""));
        var clrName = loader.Load(PluginTestHost.Workflow("""{ "id": "x", "type": "System.Diagnostics.Process" }"""));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, d => d.Code == DiagnosticCodes.UnknownActivityType);
        Assert.False(clrName.IsValid);
        // Plugins are loaded only by PluginLoader from host configuration; the workflow loader has no path to it.
        Assert.Empty(host.Plugins.Plugins);
    }

    [Fact]
    public async Task ApplicationDirectory_IsNeverScanned()
    {
        // The test output directory is full of assemblies; without a manifest it is not a plugin, and nothing is loaded.
        await using var set = await PluginTestHost.LoadAsync(new PluginSource { Directory = AppContext.BaseDirectory });

        Assert.Empty(set.Plugins);
        Assert.Equal(PluginDiagnosticCodes.ManifestMissing, Assert.Single(set.Diagnostics).Code);
    }

    [Fact]
    public async Task ChangedAssembly_AfterVerification_IsNotLoaded()
    {
        // The pin covers the directory; a DLL swapped after the directory was verified is refused at load time.
        using var staged = new StagedPlugin(Manifests.Fixture());
        await using var host = await PluginTestHost.StartAsync(staged.Source());
        var dependency = Path.Combine(staged.Directory, "MyRPA.Tests.FixtureDependency.dll");
        File.WriteAllBytes(dependency, [.. File.ReadAllBytes(dependency), 0]);

        var result = await host.RunAsync(PluginTestHost.Workflow(
            """{ "id": "n", "type": "Fixture.Dependency", "properties": { "to": "result" } }""",
            """[ { "name": "result", "direction": "Out", "type": "Object" } ]"""));

        // The runtime reports the refused load as FileLoadException for the dependency; the node fails, nothing runs.
        Assert.Equal(MyRPA.Core.Execution.ExecutionStatus.Failed, result.Status);
        Assert.Equal("FileLoadException", result.Error!.ErrorType);
        Assert.Contains("MyRPA.Tests.FixtureDependency", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Assert.Single(host.Plugins.Plugins).Context.Assemblies, a => a.GetName().Name == "MyRPA.Tests.FixtureDependency");
    }
}
