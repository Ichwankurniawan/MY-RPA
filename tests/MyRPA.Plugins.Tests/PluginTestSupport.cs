using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MyRPA.Activities;
using MyRPA.Runtime;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Validation;

namespace MyRPA.Plugins.Tests;

/// <summary>Build output directories of the fixture and sample plugins (set by the test project file).</summary>
public static class PluginPaths
{
    public static string Fixture { get; } = Metadata("FixturePluginDirectory");

    public static string Sample { get; } = Metadata("SamplePluginDirectory");

    public static string SampleWorkflow { get; } = Path.GetFullPath(Path.Combine(Sample, "..", "..", "..", "..", "demo-plugin.json"));

    private static string Metadata(string key)
    {
        var value = typeof(PluginPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;
        var path = Path.GetFullPath(value);
        Assert.True(Directory.Exists(path), $"Plugin build output not found: {path}");
        return path;
    }
}

/// <summary>Manifests for the fixture plugin; each test selects an entry type and declarations.</summary>
public static class Manifests
{
    public const string FixtureType = "MyRPA.Tests.FixturePlugin.FixturePlugin";

    public static readonly string[] FixtureActivities = ["Fixture.Dependency", "Fixture.RunState", "Fixture.Journal"];

    public static string Fixture(
        string id = "Tests.Fixture",
        string type = FixtureType,
        string[]? activities = null,
        string[]? providers = null,
        string version = "1.0.0",
        string sdkVersion = "1.0",
        string targetFramework = "net10.0",
        string capabilities = "[]",
        string dependencies = "[]",
        string assembly = "MyRPA.Tests.FixturePlugin.dll") =>
        $$"""
        {
          "manifestVersion": "1.0",
          "id": "{{id}}",
          "name": "Fixture {{id}}",
          "version": "{{version}}",
          "sdkVersion": "{{sdkVersion}}",
          "targetFramework": "{{targetFramework}}",
          "entryPoint": { "assembly": "{{assembly}}", "type": "{{type}}" },
          "capabilities": {{capabilities}},
          "activities": [{{Quote(activities ?? (type == FixtureType ? FixtureActivities : []))}}],
          "providers": [{{Quote(providers ?? (type == FixtureType ? ["Fixture.Provider"] : []))}}],
          "dependencies": {{dependencies}}
        }
        """;

    public static string Empty(string id, string dependencies = "[]", string version = "1.0.0") =>
        Fixture(id, "MyRPA.Tests.FixturePlugin.EmptyPlugin", version: version, dependencies: dependencies);

    private static string Quote(IEnumerable<string> items) => string.Join(", ", items.Select(i => $"\"{i}\""));
}

/// <summary>A temporary plugin directory: a copy of a plugin's build output with an optional manifest override.</summary>
public sealed class StagedPlugin : IDisposable
{
    public StagedPlugin(string manifest, string? from = null, bool withManifest = true)
    {
        Directory = Path.Combine(Path.GetTempPath(), "myrpa-plugin-" + Guid.NewGuid().ToString("N"));
        var source = from ?? PluginPaths.Fixture;
        foreach (var file in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(Directory, Path.GetRelativePath(source, file));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        var manifestPath = Path.Combine(Directory, "myrpa-plugin.json");
        if (withManifest)
        {
            File.WriteAllText(manifestPath, manifest);
        }
        else
        {
            File.Delete(manifestPath);
        }
    }

    public string Directory { get; }

    public static StagedPlugin Sample() => new(File.ReadAllText(Path.Combine(PluginPaths.Sample, "myrpa-plugin.json")), PluginPaths.Sample);

    public PluginSource Source(bool required = true, string? sha256 = null, params (string Key, string Value)[] settings)
    {
        var source = new PluginSource { Directory = Directory, Required = required, Sha256 = sha256 };
        foreach (var (key, value) in settings)
        {
            source.Settings[key] = value;
        }

        return source;
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: plugin assemblies are loaded from their path (ADR-0016), so on Windows they stay locked until
            // their context has unloaded.
        }
    }
}

/// <summary>A host with the real runtime, built-in activities and the given plugins.</summary>
public sealed class PluginTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private PluginTestHost(PluginSet plugins, ServiceProvider services)
    {
        Plugins = plugins;
        _services = services;
    }

    public PluginSet Plugins { get; }

    public IServiceProvider Services => _services;

    public static Task<PluginSet> LoadAsync(params PluginSource[] sources) => LoadAsync(new PluginHostOptions(), sources);

    public static Task<PluginSet> LoadAsync(PluginHostOptions options, params PluginSource[] sources)
    {
        foreach (var source in sources)
        {
            options.Sources.Add(source);
        }

        return PluginLoader.LoadAsync(options, TestContext.Current.CancellationToken);
    }

    public static async Task<PluginTestHost> StartAsync(params PluginSource[] sources)
    {
        var plugins = await LoadAsync(sources);
        return Start(plugins);
    }

    public static PluginTestHost Start(PluginSet plugins, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddMyRpaRuntime()
            .AddMyRpaActivities();
        configure?.Invoke(services);
        services.AddMyRpaPlugins(plugins);
        return new PluginTestHost(plugins, services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }));
    }

    public static string Workflow(string root, string arguments = "[]", string variables = "[]") =>
        $$"""{ "schemaVersion": "1.0", "id": "wf", "name": "Test", "version": "1.0.0", "arguments": {{arguments}}, "variables": {{variables}}, "root": {{root}} }""";

    public async Task<WorkflowExecutionResult> RunAsync(string workflowJson, IReadOnlyDictionary<string, object?>? arguments = null)
    {
        var load = _services.GetRequiredService<WorkflowLoader>().Load(workflowJson);
        Assert.True(load.IsValid, string.Join(Environment.NewLine, load.Diagnostics));
        return await _services.GetRequiredService<IWorkflowRunner>().RunAsync(
            load.Workflow!,
            new WorkflowRunRequest { Arguments = arguments ?? new Dictionary<string, object?>() },
            TestContext.Current.CancellationToken);
    }

    /// <summary>Disposes plugin services first, then the plugins themselves (the required order).</summary>
    public async ValueTask DisposeServicesAsync() => await _services.DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await Plugins.DisposeAsync();
    }
}

/// <summary>Reads fixture objects that live in a plugin context (their types are not visible to the tests).</summary>
public static class FixtureReflection
{
    public static IReadOnlyList<string> Journal(object fixturePlugin)
    {
        var journal = fixturePlugin.GetType().GetProperty("Journal")!.GetValue(fixturePlugin)!;
        return (IReadOnlyList<string>)journal.GetType().GetProperty("Events")!.GetValue(journal)!;
    }
}
