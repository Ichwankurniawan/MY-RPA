using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyRPA.Activities;
using MyRPA.Contracts.Orchestration;
using MyRPA.Core.Activities;
using MyRPA.Orchestration.Sqlite;
using MyRPA.Workflow.Validation;

namespace MyRPA.Orchestration.Tests;

/// <summary>Deterministic ids: prefix_0001, prefix_0002, …</summary>
internal sealed class SequentialIds : IOrchestrationIds
{
    private int _next;

    public string NewId(string prefix) => $"{prefix}_{Interlocked.Increment(ref _next):D4}";
}

/// <summary>The built-in catalog, plus a plugin activity and an activity with a secret property.</summary>
internal static class TestCatalog
{
    public const string PluginActivity = "Demo.Thing";

    private static readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddMyRpaActivities().BuildServiceProvider();

    public static IActivityCatalog Instance { get; } = new Combined(
        _services.GetRequiredService<IActivityCatalog>(),
        [
            new ActivityDescriptor(new ActivityTypeName(PluginActivity), "Thing", "Demo", "A plugin activity.", []),
            new ActivityDescriptor(new ActivityTypeName("Test.Login"), "Log in", "Test", "Uses a secret.", [new ActivityPropertyDefinition("password", ActivityPropertyKind.Expression, isRequired: true) { IsSecret = true }]),
        ]);

    public static WorkflowLoader Loader { get; } = new(Instance);

    /// <summary>Activity types a fully equipped Runner reports.</summary>
    public static IReadOnlyList<string> AllActivities { get; } = [.. Instance.Descriptors.Select(d => d.TypeName.Value)];

    /// <summary>Activity types a Runner without the demo plugin reports.</summary>
    public static IReadOnlyList<string> BuiltInActivities { get; } = [.. AllActivities.Where(a => a.StartsWith("Core.", StringComparison.Ordinal))];

    public static PackagePlugin? PluginOf(string activity) => activity == PluginActivity ? new PackagePlugin("Demo.Plugin", "1.2.0") : null;

    private sealed class Combined(IActivityCatalog inner, IReadOnlyList<ActivityDescriptor> extra) : IActivityCatalog
    {
        public IReadOnlyList<ActivityDescriptor> Descriptors { get; } = [.. inner.Descriptors, .. extra];

        public bool TryGet(ActivityTypeName typeName, [NotNullWhen(true)] out ActivityDescriptor? descriptor)
        {
            if (inner.TryGet(typeName, out descriptor))
            {
                return true;
            }

            descriptor = extra.FirstOrDefault(d => d.TypeName == typeName);
            return descriptor is not null;
        }
    }
}

/// <summary>Workflow documents used by the tests.</summary>
internal static class Workflows
{
    public static string Hello(string id = "hello", string name = "Hello") => $$"""
        { "schemaVersion": "1.0", "id": "{{id}}", "name": "{{name}}", "version": "1.0.0",
          "arguments": [
            { "name": "who", "direction": "In", "type": "String", "default": "World" },
            { "name": "count", "direction": "In", "type": "Int" },
            { "name": "greeting", "direction": "Out", "type": "String" } ],
          "root": { "id": "main", "type": "Core.Sequence", "children": [
            { "id": "assign", "type": "Core.Assign", "properties": { "to": "greeting", "value": "'Hello, ' + who" } } ] } }
        """;

    public static string Required() => """
        { "schemaVersion": "1.0", "id": "required", "name": "Required", "version": "1.0.0",
          "arguments": [ { "name": "customer", "direction": "In", "type": "String", "required": true } ],
          "root": { "id": "main", "type": "Core.Log", "properties": { "message": "customer" } } }
        """;

    public static string WithPlugin() => """
        { "schemaVersion": "1.0", "id": "plugin", "name": "Uses a plugin", "version": "1.0.0",
          "root": { "id": "main", "type": "Core.Sequence", "children": [ { "id": "thing", "type": "Demo.Thing" } ] } }
        """;

    public static string WithSecret() => """
        { "schemaVersion": "1.0", "id": "secret", "name": "Secret", "version": "1.0.0",
          "arguments": [ { "name": "password", "direction": "In", "type": "String" } ],
          "root": { "id": "main", "type": "Test.Login", "properties": { "password": "password" } } }
        """;
}

/// <summary>An orchestrator on a temporary SQLite file with a fake clock. Reopening the same folder simulates a restart.</summary>
internal sealed class OrchestratorFixture : IAsyncDisposable
{
    private readonly bool _ownsFolder;

    private OrchestratorFixture(string folder, FakeTimeProvider time, SqliteOrchestrationStore store, Orchestrator orchestrator, bool ownsFolder)
    {
        Folder = folder;
        Time = time;
        Store = store;
        Orchestrator = orchestrator;
        _ownsFolder = ownsFolder;
    }

    public static DateTimeOffset Start { get; } = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    public string Folder { get; }

    public FakeTimeProvider Time { get; }

    public SqliteOrchestrationStore Store { get; }

    public Orchestrator Orchestrator { get; }

    public static Actor Admin => Actor.LocalOwner;

    public static async Task<OrchestratorFixture> CreateAsync(string? folder = null, FakeTimeProvider? time = null, IOrchestrationIds? ids = null)
    {
        var owns = folder is null;
        folder ??= Path.Combine(Path.GetTempPath(), "myrpa-orch-" + Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        time ??= new FakeTimeProvider(Start);
        var store = new SqliteOrchestrationStore(Path.Combine(folder, "orchestration.db"));
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var orchestrator = new Orchestrator(store, ids ?? new SequentialIds(), time, TestCatalog.Loader, new OrchestratorOptions { LeaseDuration = TimeSpan.FromSeconds(30) }, NullLogger<Orchestrator>.Instance);
        return new OrchestratorFixture(folder, time, store, orchestrator, owns);
    }

    public Task<(ProcessRecord Process, PackageRecord Package)> PublishAsync(string entryJson, params (string Path, string Json)[] more) =>
        Orchestrator.PublishAsync(Admin, "project", ["main.json"], [new("main.json", entryJson), .. more.Select(m => new KeyValuePair<string, string>(m.Path, m.Json))], null, TestCatalog.PluginOf, TestContext.Current.CancellationToken);

    public Task<(ProcessRecord Process, PackageRecord Package)> PublishProjectAsync(string[] entryPoints, params (string Path, string Json)[] files) =>
        Orchestrator.PublishAsync(Admin, "project", entryPoints, [.. files.Select(m => new KeyValuePair<string, string>(m.Path, m.Json))], null, TestCatalog.PluginOf, TestContext.Current.CancellationToken);

    public async Task<ProcessRecord> DeployedAsync(string entryJson)
    {
        var (process, package) = await PublishAsync(entryJson);
        return await Orchestrator.DeployAsync(Admin, process.ProcessId, package.Version, TestContext.Current.CancellationToken);
    }

    public async Task<JobRecord> SubmitAsync(string processId, string? key = null, int? version = null)
    {
        var (job, _) = await Orchestrator.SubmitAsync(Admin, new SubmitJobRequest { ProcessId = processId, IdempotencyKey = key, Version = version }, TestContext.Current.CancellationToken);
        return job;
    }

    /// <summary>Creates a Runner and registers it with the given capabilities.</summary>
    public async Task<RunnerRecord> RunnerAsync(string name, string? group = null, int capacity = 1, IReadOnlyList<string>? activities = null, IReadOnlyList<RunnerPluginInfo>? plugins = null, IReadOnlyList<RunnerActiveJob>? held = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var (runner, _) = await Orchestrator.CreateRunnerAsync(Admin, name, group, capacity, ct);
        await RegisterAsync(runner, capacity, activities, plugins, held);
        return (await Store.GetRunnerAsync(runner.RunnerId, ct))!;
    }

    public Task<RunnerRegisterResponse> RegisterAsync(RunnerRecord runner, int capacity = 1, IReadOnlyList<string>? activities = null, IReadOnlyList<RunnerPluginInfo>? plugins = null, IReadOnlyList<RunnerActiveJob>? held = null, IReadOnlyList<string>? formats = null) =>
        Orchestrator.RegisterAsync(runner, new RunnerRegisterRequest
        {
            ProtocolVersion = RunnerProtocol.Version,
            RunnerVersion = "0.1.0",
            OperatingSystem = "Test OS",
            Architecture = "X64",
            MaxConcurrency = capacity,
            Activities = activities ?? TestCatalog.AllActivities,
            Plugins = plugins ?? [new RunnerPluginInfo("Demo.Plugin", "1.2.0")],
            ActiveJobs = held ?? [],
            PackageFormats = formats ?? [WorkflowPackage.FormatName],
        }, TestContext.Current.CancellationToken);

    public Task<JobAssignment?> ClaimAsync(RunnerRecord runner) => Orchestrator.ClaimAsync(runner, TimeSpan.Zero, TestContext.Current.CancellationToken);

    public async Task<JobRecord> JobAsync(string jobId) => (await Store.GetJobAsync(jobId, TestContext.Current.CancellationToken))!;

    public async ValueTask DisposeAsync()
    {
        Orchestrator.Dispose();
        await Store.DisposeAsync();
        if (_ownsFolder)
        {
            TryDelete(Folder);
        }
    }

    public static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a temporary folder.
        }
    }
}
