using Microsoft.Data.Sqlite;
using MyRPA.Orchestration.Sqlite;

namespace MyRPA.Orchestration.Tests;

/// <summary>A package is a project with entry points; one workflow is a project with a single entry point.</summary>
public sealed class EntryPointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly (string Path, string Json)[] _project =
    [
        ("main.json", Workflows.Hello()),
        ("orders/import.json", Workflows.Required()),
        ("helper.json", Workflows.Hello(id: "helper", name: "Helper")),
    ];

    [Fact]
    public async Task Publish_AProject_IsOneProcessPerProject_WithItsEntryPoints()
    {
        await using var f = await OrchestratorFixture.CreateAsync();

        var (process, first) = await f.PublishProjectAsync(["main.json", "orders/import.json"], _project);
        var (again, second) = await f.PublishProjectAsync(["main.json"], _project);

        Assert.Equal(("project", "project"), (process.Name, process.Project));
        Assert.Equal(process.ProcessId, again.ProcessId);
        Assert.Equal((1, 2), (first.Version, second.Version));
        Assert.Equal(["main.json", "orders/import.json"], first.Manifest.EntryPoints.Select(e => e.Path));
        Assert.Equal(["main.json"], second.Manifest.EntryPoints.Select(e => e.Path));
        Assert.Equal(3, first.Manifest.Files.Count);
        Assert.Single(await f.Store.ListProcessesAsync(Ct));
    }

    [Fact]
    public async Task Submit_WithSeveralEntryPoints_NeedsTheEntryPoint_AndRunsThatWorkflow()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var (process, package) = await f.PublishProjectAsync(["main.json", "orders/import.json"], _project);
        await f.Orchestrator.DeployAsync(OrchestratorFixture.Admin, process.ProcessId, package.Version, Ct);

        var unnamed = await Assert.ThrowsAsync<OrchestrationException>(() => f.SubmitAsync(process.ProcessId));
        Assert.Equal(OrchestrationFailure.Invalid, unnamed.Failure);
        Assert.Contains("2 entry points", unnamed.Message, StringComparison.Ordinal);

        var unknown = await Assert.ThrowsAsync<OrchestrationException>(() => f.Orchestrator.SubmitAsync(
            OrchestratorFixture.Admin, new SubmitJobRequest { ProcessId = process.ProcessId, EntryPath = "helper.json" }, Ct));
        Assert.Equal(OrchestrationFailure.Invalid, unknown.Failure);

        // The entry point's own arguments are checked: import.json requires 'customer'.
        var missing = await Assert.ThrowsAsync<OrchestrationException>(() => f.Orchestrator.SubmitAsync(
            OrchestratorFixture.Admin, new SubmitJobRequest { ProcessId = process.ProcessId, EntryPath = "orders/import.json" }, Ct));
        Assert.Contains("customer", missing.Message, StringComparison.Ordinal);

        var import = new SubmitJobRequest
        {
            ProcessId = process.ProcessId,
            EntryPath = "orders/import.json",
            ArgumentText = new Dictionary<string, string> { ["customer"] = "ACME" },
        };
        var (cancelled, _) = await f.Orchestrator.SubmitAsync(OrchestratorFixture.Admin, import, Ct);
        await f.Orchestrator.CancelAsync(OrchestratorFixture.Admin, cancelled.JobId, Ct);
        var (retry, _) = await f.Orchestrator.RetryAsync(OrchestratorFixture.Admin, cancelled.JobId, null, Ct);
        Assert.Equal(("orders/import.json", "orders/import.json"), (cancelled.EntryPath, retry.EntryPath));

        var runner = await f.RunnerAsync("r1");
        var assignment = await f.ClaimAsync(runner);
        Assert.Equal((retry.JobId, "orders/import.json"), (assignment!.JobId, assignment.EntryPath));
    }

    [Fact]
    public async Task Submit_WithASingleEntryPoint_NeedsNoEntryPoint()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());

        var job = await f.SubmitAsync(process.ProcessId);

        Assert.Equal("main.json", job.EntryPath);
    }

    [Fact]
    public async Task Schedule_TakesArgumentsAsTypedText_AndItsJobsGetThem()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        var definition = new ScheduleDefinition
        {
            Name = "hourly",
            Kind = ScheduleKind.Cron,
            Cron = "0 * * * *",
            TimeZone = "UTC",
            ArgumentText = new Dictionary<string, string> { ["who"] = "Ada", ["count"] = "3" },
        };

        var bad = await Assert.ThrowsAsync<OrchestrationException>(() => f.Orchestrator.CreateScheduleAsync(
            OrchestratorFixture.Admin, process.ProcessId, definition with { ArgumentText = new Dictionary<string, string> { ["count"] = "three" } }, Ct));
        Assert.Contains("Argument 'count'", bad.Message, StringComparison.Ordinal);
        var schedule = await f.Orchestrator.CreateScheduleAsync(OrchestratorFixture.Admin, process.ProcessId, definition, Ct);
        f.Time.Advance(TimeSpan.FromHours(1));
        await f.Orchestrator.EvaluateSchedulesAsync(Ct);

        using var stored = System.Text.Json.JsonDocument.Parse(schedule.ArgumentsJson);
        Assert.Equal(("Ada", 3), (stored.RootElement.GetProperty("who").GetString(), stored.RootElement.GetProperty("count").GetInt32()));
        Assert.Equal(schedule.ArgumentsJson, Assert.Single((await f.Store.QueryJobsAsync(new JobQuery(), Ct)).Jobs).ArgumentsJson);
    }

    [Fact]
    public async Task Schedule_KeepsItsEntryPoint_AndSkipsWhenTheDeployedVersionDropsIt()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var (process, v1) = await f.PublishProjectAsync(["main.json", "orders/import.json"], _project);
        await f.Orchestrator.DeployAsync(OrchestratorFixture.Admin, process.ProcessId, v1.Version, Ct);
        var definition = new ScheduleDefinition
        {
            Name = "hourly import",
            Kind = ScheduleKind.Cron,
            Cron = "0 * * * *",
            TimeZone = "UTC",
            EntryPath = "orders/import.json",
            Overlap = OverlapPolicy.Allow,
            Arguments = new Dictionary<string, System.Text.Json.JsonElement> { ["customer"] = System.Text.Json.JsonSerializer.SerializeToElement("ACME") },
        };

        var refused = await Assert.ThrowsAsync<OrchestrationException>(() => f.Orchestrator.CreateScheduleAsync(OrchestratorFixture.Admin, process.ProcessId, definition with { EntryPath = null }, Ct));
        Assert.Contains("entry points", refused.Message, StringComparison.Ordinal);
        var schedule = await f.Orchestrator.CreateScheduleAsync(OrchestratorFixture.Admin, process.ProcessId, definition, Ct);
        Assert.Equal("orders/import.json", schedule.EntryPath);

        f.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await f.Orchestrator.EvaluateSchedulesAsync(Ct));
        Assert.Equal("orders/import.json", Assert.Single((await f.Store.QueryJobsAsync(new JobQuery(), Ct)).Jobs).EntryPath);

        var (_, v2) = await f.PublishProjectAsync(["main.json"], _project);
        await f.Orchestrator.DeployAsync(OrchestratorFixture.Admin, process.ProcessId, v2.Version, Ct);
        f.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await f.Orchestrator.EvaluateSchedulesAsync(Ct));
        var outcome = (await f.Store.GetScheduleAsync(schedule.ScheduleId, Ct))!.LastOutcome;
        Assert.StartsWith("Skipped", outcome, StringComparison.Ordinal);
        Assert.Contains("orders/import.json", outcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunnerThatDoesNotSayWhatItReads_IsNotAssignedNewPackages_AndTheWaitSaysWhy()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        var job = await f.SubmitAsync(process.ProcessId);
        var (old, _) = await f.Orchestrator.CreateRunnerAsync(OrchestratorFixture.Admin, "old-runner", null, 1, Ct);
        await f.RegisterAsync(old, formats: []);

        Assert.Null(await f.ClaimAsync((await f.Store.GetRunnerAsync(old.RunnerId, Ct))!));
        Assert.Equal([WorkflowPackage.FirstFormatName], (await f.Store.GetRunnerAsync(old.RunnerId, Ct))!.PackageFormats);
        Assert.Contains(await f.Orchestrator.ExplainWaitAsync(job, Ct), w => w.Contains("not myrpa-package/2: update this Runner", StringComparison.Ordinal));

        var current = await f.RunnerAsync("new-runner");
        Assert.Equal(job.JobId, (await f.ClaimAsync(current))!.JobId);
    }

    [Fact]
    public async Task Publish_AnInvalidEntryPoint_IsRefused_AndCreatesNoProcess()
    {
        await using var f = await OrchestratorFixture.CreateAsync();

        var ex = await Assert.ThrowsAsync<OrchestrationException>(() => f.PublishProjectAsync(["main.json", "broken.json"], ("main.json", Workflows.Hello()), ("broken.json", "{}")));

        Assert.Equal(OrchestrationFailure.Invalid, ex.Failure);
        Assert.Empty(await f.Store.ListProcessesAsync(Ct));
    }

    [Fact]
    public async Task Initialize_ASchemaOneStore_DropsProcessDataAndKeepsRunnersTokensAndAudit()
    {
        var folder = Path.Combine(Path.GetTempPath(), "myrpa-orch-" + Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "orchestration.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync(Ct);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE processes (id TEXT PRIMARY KEY, project TEXT NOT NULL, entry_path TEXT NOT NULL, revision INTEGER NOT NULL, json TEXT NOT NULL, UNIQUE (project, entry_path));
                    CREATE TABLE packages (process_id TEXT NOT NULL, version INTEGER NOT NULL, sha256 TEXT NOT NULL, json TEXT NOT NULL, content BLOB NOT NULL, PRIMARY KEY (process_id, version));
                    CREATE TRIGGER packages_no_delete BEFORE DELETE ON packages BEGIN SELECT RAISE(ABORT, 'published packages are immutable'); END;
                    CREATE TABLE jobs (id TEXT PRIMARY KEY, state TEXT NOT NULL, process_id TEXT NOT NULL, runner_id TEXT, schedule_id TEXT, idempotency_key TEXT UNIQUE,
                        created_ms INTEGER NOT NULL, completed_ms INTEGER, revision INTEGER NOT NULL, json TEXT NOT NULL);
                    CREATE TABLE job_events (job_id TEXT NOT NULL, attempt INTEGER NOT NULL, sequence INTEGER NOT NULL, kind TEXT NOT NULL, time_ms INTEGER NOT NULL, json TEXT NOT NULL, PRIMARY KEY (job_id, attempt, sequence));
                    CREATE TABLE runners (id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, revision INTEGER NOT NULL, json TEXT NOT NULL);
                    CREATE TABLE schedules (id TEXT PRIMARY KEY, revision INTEGER NOT NULL, json TEXT NOT NULL);
                    CREATE TABLE access_tokens (id TEXT PRIMARY KEY, revision INTEGER NOT NULL, json TEXT NOT NULL);
                    CREATE TABLE audit (id INTEGER PRIMARY KEY AUTOINCREMENT, time_ms INTEGER NOT NULL, actor TEXT NOT NULL, action TEXT NOT NULL, target TEXT, details TEXT);
                    CREATE TABLE settings (key TEXT PRIMARY KEY, json TEXT NOT NULL);
                    INSERT INTO processes VALUES ('prc_old', 'project', 'main.json', 0, '{}');
                    INSERT INTO packages VALUES ('prc_old', 1, 'x', '{}', x'00');
                    INSERT INTO runners VALUES ('rnr_kept', 'kept', 0, '{}');
                    INSERT INTO access_tokens VALUES ('tok_kept', 0, '{}');
                    INSERT INTO audit (time_ms, actor, action) VALUES (0, 'owner', 'runner.create');
                    PRAGMA user_version = 1;
                    """;
                await command.ExecuteNonQueryAsync(Ct);
            }

            await using var store = new SqliteOrchestrationStore(path);
            await store.InitializeAsync(Ct);

            var diagnostics = await store.GetDiagnosticsAsync(Ct);
            Assert.Equal(2, diagnostics.SchemaVersion);
            Assert.Equal(
                (0L, 0L, 1L, 1L, 1L),
                (diagnostics.Counts["processes"], diagnostics.Counts["packages"], diagnostics.Counts["runners"], diagnostics.Counts["access_tokens"], diagnostics.Counts["audit"]));
        }
        finally
        {
            OrchestratorFixture.TryDelete(folder);
        }
    }
}
