using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyRPA.Activities;
using MyRPA.Core.Activities;
using MyRPA.Core.Execution;
using MyRPA.Sdk.Plugins;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Validation;

namespace MyRPA.Plugins.Tests;

/// <summary>Load → Initialize → Register → Use → Dispose, isolation of failures, and assembly loading.</summary>
public sealed class PluginLifecycleTests
{
    private static string Node(string type, string id = "n", string to = "result") =>
        $$"""{ "id": "{{id}}", "type": "{{type}}", "properties": { "to": "{{to}}" } }""";

    private static readonly string _resultArgument = """[ { "name": "result", "direction": "Out", "type": "Object" } ]""";

    private static IEnumerable<string> Codes(PluginSet set) =>
        set.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Code);

    [Fact]
    public async Task Plugin_IsInitializedRegisteredAndRunsThroughTheEngine()
    {
        using var staged = new StagedPlugin(Manifests.Fixture());
        await using var host = await PluginTestHost.StartAsync(staged.Source(settings: ("mode", "custom")));

        var result = await host.RunAsync(PluginTestHost.Workflow(Node("Fixture.Journal"), _resultArgument));

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var journal = Assert.IsAssignableFrom<IReadOnlyList<object?>>(result.Outputs["result"]);
        Assert.Equal(["plugin:initialize:Tests.Fixture:custom:1.0", "plugin:register", "provider:created"], journal);
    }

    [Fact]
    public async Task PluginActivities_AreInTheCatalogWithTheirSource()
    {
        using var staged = new StagedPlugin(Manifests.Fixture());
        await using var host = await PluginTestHost.StartAsync(staged.Source());

        var catalog = host.Services.GetRequiredService<ActivityCatalog>();

        Assert.All(Manifests.FixtureActivities, name =>
            Assert.Equal("Tests.Fixture", catalog.Registrations.Single(r => r.Descriptor.TypeName.Value == name).Source));
        Assert.Contains(catalog.Registrations, r => r.Descriptor.TypeName.Value == "Core.Sequence" && r.Source is null);
        Assert.Same(host.Plugins, host.Services.GetRequiredService<IPluginRegistry>());
    }

    [Fact]
    public async Task PluginCode_LivesInItsOwnCollectibleContext_AndSharesTheSdkWithTheHost()
    {
        using var staged = new StagedPlugin(Manifests.Fixture());
        await using var set = await PluginTestHost.LoadAsync(staged.Source());
        var plugin = Assert.Single(set.Plugins);

        var context = AssemblyLoadContext.GetLoadContext(plugin.Instance.GetType().Assembly)!;
        Assert.Same(plugin.Context, context);
        Assert.NotSame(AssemblyLoadContext.Default, context);
        Assert.True(context.IsCollectible);
        Assert.Equal("MyRPA.Plugin:Tests.Fixture", context.Name);

        // The fixture directory contains its own MyRPA.Sdk.dll; it must be ignored so that the plugin implements the
        // host's IPlugin (type identity), which is why the cast in the loader succeeded.
        Assert.IsAssignableFrom<IPlugin>(plugin.Instance);
        Assert.DoesNotContain(context.Assemblies, a => a.GetName().Name is "MyRPA.Sdk" or "MyRPA.Core" or "MyRPA.Workflow");
    }

    [Fact]
    public async Task PluginAssemblies_AreLoadedFromTheirVerifiedPath()
    {
        // ADR-0016: libraries that locate companion files next to themselves (e.g. Playwright's driver) need a real
        // Assembly.Location inside the plugin directory.
        using var staged = new StagedPlugin(Manifests.Fixture());
        await using var host = await PluginTestHost.StartAsync(staged.Source());
        await host.RunAsync(PluginTestHost.Workflow(Node("Fixture.Dependency"), _resultArgument));

        var context = Assert.Single(host.Plugins.Plugins).Context;

        Assert.Equal(2, context.Assemblies.Count());
        Assert.All(context.Assemblies, a => Assert.Equal(
            Path.Combine(staged.Directory, a.GetName().Name + ".dll"), a.Location, ignoreCase: OperatingSystem.IsWindows()));
    }

    [Fact]
    public async Task OnlyTheEntryAssemblyIsLoaded_OtherFilesAreNever()
    {
        using var staged = new StagedPlugin(Manifests.Fixture());
        // A DLL in the directory that no dependency graph refers to.
        File.Copy(Path.Combine(staged.Directory, "MyRPA.Tests.FixtureDependency.dll"), Path.Combine(staged.Directory, "Stray.dll"));
        await using var set = await PluginTestHost.LoadAsync(staged.Source());

        var context = Assert.Single(set.Plugins).Context;

        Assert.Equal(["MyRPA.Tests.FixturePlugin"], context.Assemblies.Select(a => a.GetName().Name));
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies, a => a.GetName().Name!.StartsWith("MyRPA.Tests.Fixture", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrivateDependency_ResolvesIntoThePluginContext()
    {
        using var staged = new StagedPlugin(Manifests.Fixture());
        await using var host = await PluginTestHost.StartAsync(staged.Source());

        var result = await host.RunAsync(PluginTestHost.Workflow(Node("Fixture.Dependency"), _resultArgument));

        Assert.Equal("MyRPA.Plugin:Tests.Fixture", result.Outputs["result"]);
        Assert.Contains(Assert.Single(host.Plugins.Plugins).Context.Assemblies, a => a.GetName().Name == "MyRPA.Tests.FixtureDependency");
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies, a => a.GetName().Name == "MyRPA.Tests.FixtureDependency");
    }

    [Fact]
    public async Task TwoPlugins_LoadTheSameAssemblyIntoSeparateContexts()
    {
        using var first = new StagedPlugin(Manifests.Fixture());
        using var second = new StagedPlugin(Manifests.Empty("Tests.Second"));
        await using var set = await PluginTestHost.LoadAsync(first.Source(), second.Source());

        var types = set.Plugins.Select(p => p.Instance.GetType().Assembly).ToList();

        Assert.Equal(2, types.Count);
        Assert.NotSame(types[0], types[1]);
        Assert.Equal(types[0].GetName().Name, types[1].GetName().Name);
    }

    [Fact]
    public async Task RunLifetimeServices_ArePerRun_AndDisposedWhenTheRunEnds()
    {
        using var staged = new StagedPlugin(Manifests.Fixture());
        await using var host = await PluginTestHost.StartAsync(staged.Source());
        var workflow = PluginTestHost.Workflow(
            """
            { "id": "s", "type": "Core.Sequence", "children": [
              { "id": "a", "type": "Fixture.RunState", "properties": { "to": "first" } },
              { "id": "b", "type": "Fixture.RunState", "properties": { "to": "second" } } ] }
            """,
            """[ { "name": "first", "direction": "Out", "type": "String" }, { "name": "second", "direction": "Out", "type": "String" } ]""");

        var run1 = await host.RunAsync(workflow);
        var run2 = await host.RunAsync(workflow);
        var journal = await host.RunAsync(PluginTestHost.Workflow(Node("Fixture.Journal"), _resultArgument));

        Assert.Equal(run1.Outputs["first"], run1.Outputs["second"]);
        Assert.NotEqual(run1.Outputs["first"], run2.Outputs["first"]);
        var events = ((IReadOnlyList<object?>)journal.Outputs["result"]!).Cast<string>().Where(e => e.StartsWith("run:", StringComparison.Ordinal));
        Assert.Equal(["run:created:1", "run:disposed:1", "run:created:2", "run:disposed:2"], events);
    }

    [Fact]
    public async Task Dispose_DisposesServicesThenThePlugin()
    {
        using var staged = new StagedPlugin(Manifests.Fixture());
        var host = await PluginTestHost.StartAsync(staged.Source());
        await host.RunAsync(PluginTestHost.Workflow(Node("Fixture.Journal"), _resultArgument));
        var instance = Assert.Single(host.Plugins.Plugins).Instance;

        await host.DisposeServicesAsync();
        Assert.Equal("provider:disposed", FixtureReflection.Journal(instance)[^1]);

        await host.Plugins.DisposeAsync();
        Assert.Equal(["provider:disposed", "plugin:disposed"], FixtureReflection.Journal(instance).TakeLast(2));
    }

    [Fact]
    public async Task InitializeFailure_IsIsolated()
    {
        using var broken = new StagedPlugin(Manifests.Fixture("Tests.Broken", "MyRPA.Tests.FixturePlugin.ThrowingInitializePlugin", activities: [], providers: []));
        using var good = new StagedPlugin(Manifests.Fixture());
        await using var host = PluginTestHost.Start(await PluginTestHost.LoadAsync(broken.Source(required: false), good.Source()));

        var diagnostic = Assert.Single(host.Plugins.Diagnostics);
        Assert.Equal(PluginDiagnosticCodes.InitializationFailed, diagnostic.Code);
        Assert.Contains("boom in Initialize", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(ExecutionStatus.Succeeded, (await host.RunAsync(PluginTestHost.Workflow(Node("Fixture.Journal"), _resultArgument))).Status);
    }

    [Fact]
    public async Task RegisterFailure_AppliesNothing()
    {
        using var broken = new StagedPlugin(Manifests.Fixture("Tests.Broken", "MyRPA.Tests.FixturePlugin.ThrowingRegisterPlugin", activities: ["Fixture.Dependency"], providers: []));
        await using var host = PluginTestHost.Start(await PluginTestHost.LoadAsync(broken.Source(required: false)));

        Assert.Equal([PluginDiagnosticCodes.RegistrationFailed], Codes(host.Plugins));
        Assert.False(host.Services.GetRequiredService<IActivityCatalog>().TryGet(new ActivityTypeName("Fixture.Dependency"), out _));
    }

    [Theory]
    [InlineData("MyRPA.Tests.FixturePlugin.UndeclaredActivityPlugin", new string[0], "registered but not declared")]
    [InlineData("MyRPA.Tests.FixturePlugin.EmptyPlugin", new[] { "Fixture.Promised" }, "declared in the manifest but not registered")]
    [InlineData("MyRPA.Tests.FixturePlugin.HostServicePlugin", new string[0], "not defined by the plugin")]
    public async Task InvalidRegistrations_RejectThePlugin(string type, string[] activities, string expected)
    {
        using var staged = new StagedPlugin(Manifests.Fixture("Tests.Bad", type, activities: activities, providers: []));
        await using var host = PluginTestHost.Start(await PluginTestHost.LoadAsync(staged.Source(required: false)));

        var diagnostic = Assert.Single(host.Plugins.Diagnostics);
        Assert.Equal(PluginDiagnosticCodes.RegistrationFailed, diagnostic.Code);
        Assert.Contains(expected, diagnostic.Message, StringComparison.Ordinal);
        // The host's own services are untouched.
        Assert.DoesNotContain("Hijacking", host.Services.GetRequiredService<ILoggerFactory>().GetType().Name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MyRPA.Tests.FixturePlugin.NotAPlugin", "does not implement")]
    [InlineData("MyRPA.Tests.FixturePlugin.NoDefaultConstructorPlugin", "public parameterless constructor")]
    [InlineData("MyRPA.Tests.FixturePlugin.DoesNotExist", "was not found")]
    public async Task InvalidEntryPoint_IsRejected(string type, string expected)
    {
        using var staged = new StagedPlugin(Manifests.Fixture("Tests.Bad", type, activities: [], providers: []));
        await using var set = await PluginTestHost.LoadAsync(staged.Source());

        var diagnostic = Assert.Single(set.Diagnostics);
        Assert.Equal(PluginDiagnosticCodes.EntryPointInvalid, diagnostic.Code);
        Assert.Contains(expected, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptEntryAssembly_IsALoadFailure()
    {
        using var staged = new StagedPlugin(Manifests.Fixture());
        File.WriteAllText(Path.Combine(staged.Directory, "MyRPA.Tests.FixturePlugin.dll"), "not an assembly");
        await using var set = await PluginTestHost.LoadAsync(staged.Source());

        Assert.Equal([PluginDiagnosticCodes.LoadFailed], Codes(set));
    }

    [Fact]
    public async Task PluginActivity_ConflictingWithAHostActivity_FailsComposition()
    {
        using var staged = new StagedPlugin(Manifests.Fixture());
        await using var set = await PluginTestHost.LoadAsync(staged.Source());

        var error = Assert.Throws<InvalidOperationException>(() => PluginTestHost.Start(
            set,
            services => services.AddActivity<HostActivity>(new ActivityDescriptor(new ActivityTypeName("Fixture.Journal"), "Host", "Test"))));

        Assert.Contains("Fixture.Journal", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PluginSet_CanBeAppliedOnlyOnce()
    {
        await using var set = await PluginTestHost.LoadAsync();
        set.AddTo(new ServiceCollection());

        Assert.Throws<InvalidOperationException>(() => set.AddTo(new ServiceCollection()));
    }

    [Fact]
    public async Task Plugin_UnloadsAfterDisposal()
    {
        using var staged = new StagedPlugin(Manifests.Fixture());
        var contexts = await LoadRunAndDisposeAsync(staged.Source());

        for (var i = 0; i < 20 && contexts.Any(c => c.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.All(contexts, c => Assert.False(c.IsAlive, "The plugin context was not unloaded."));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<IReadOnlyList<WeakReference>> LoadRunAndDisposeAsync(PluginSource source)
    {
        var host = await PluginTestHost.StartAsync(source);
        var result = await host.RunAsync(PluginTestHost.Workflow(
            """
            { "id": "s", "type": "Core.Sequence", "children": [
              { "id": "a", "type": "Fixture.Journal", "properties": { "to": "result" } },
              { "id": "b", "type": "Fixture.Dependency", "properties": { "to": "result" } },
              { "id": "c", "type": "Fixture.RunState", "properties": { "to": "result" } } ] }
            """,
            _resultArgument));
        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var contexts = host.Plugins.ContextReferences();
        await host.DisposeAsync();
        return contexts;
    }

    public sealed class HostActivity : IActivity
    {
        public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context) => ActivityResult.CompletedTask;
    }
}
