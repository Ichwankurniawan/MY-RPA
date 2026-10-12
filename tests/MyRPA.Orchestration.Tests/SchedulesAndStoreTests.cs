using Microsoft.Data.Sqlite;
using MyRPA.Orchestration.Sqlite;

namespace MyRPA.Orchestration.Tests;

public sealed class ScheduleEvaluationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScheduleDefinition Hourly(MisfirePolicy misfire = MisfirePolicy.RunOnce, OverlapPolicy overlap = OverlapPolicy.Allow) => new()
    {
        Name = "hourly",
        Kind = ScheduleKind.Cron,
        Cron = "0 * * * *",
        TimeZone = "Europe/Amsterdam",
        Misfire = misfire,
        Overlap = overlap,
    };

    [Fact]
    public async Task Cron_EachFireTimeCreatesExactlyOneJob_EvenWhenEvaluatedTwice()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        var schedule = await f.Orchestrator.CreateScheduleAsync(OrchestratorFixture.Admin, process.ProcessId, Hourly(), Ct);
        Assert.Equal(OrchestratorFixture.Start.AddHours(1), schedule.NextFireAt);

        f.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await f.Orchestrator.EvaluateSchedulesAsync(Ct));
        Assert.Equal(0, await f.Orchestrator.EvaluateSchedulesAsync(Ct));

        var job = Assert.Single((await f.Store.QueryJobsAsync(new JobQuery { ScheduleId = schedule.ScheduleId }, Ct)).Jobs);
        Assert.Equal(("schedule", OrchestratorFixture.Start.AddHours(1)), (job.Source, job.ScheduledFor));
        Assert.Equal(OrchestratorFixture.Start.AddHours(2), (await f.Store.GetScheduleAsync(schedule.ScheduleId, Ct))!.NextFireAt);
    }

    [Fact]
    public async Task Restart_DoesNotCreateTheSameFireTimeAgain()
    {
        var folder = Path.Combine(Path.GetTempPath(), "myrpa-orch-" + Path.GetRandomFileName());
        try
        {
            var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(OrchestratorFixture.Start);
            await using (var before = await OrchestratorFixture.CreateAsync(folder, time))
            {
                var process = await before.DeployedAsync(Workflows.Hello());
                await before.Orchestrator.CreateScheduleAsync(OrchestratorFixture.Admin, process.ProcessId, Hourly(), Ct);
                time.Advance(TimeSpan.FromHours(1));
                Assert.Equal(1, await before.Orchestrator.EvaluateSchedulesAsync(Ct));
            }

            await using var after = await OrchestratorFixture.CreateAsync(folder, time, new SequentialIds());
            Assert.Equal(0, await after.Orchestrator.EvaluateSchedulesAsync(Ct));
            Assert.Equal(1, (await after.Store.QueryJobsAsync(new JobQuery(), Ct)).Total);
        }
        finally
        {
            OrchestratorFixture.TryDelete(folder);
        }
    }

    [Fact]
    public async Task MissedFireTimes_RunOnce_CreatesOneJobForTheLatest()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        var schedule = await f.Orchestrator.CreateScheduleAsync(OrchestratorFixture.Admin, process.ProcessId, Hourly(), Ct);

        f.Time.Advance(TimeSpan.FromHours(5).Add(TimeSpan.FromMinutes(10)));
        Assert.Equal(1, await f.Orchestrator.EvaluateSchedulesAsync(Ct));

        var job = Assert.Single((await f.Store.QueryJobsAsync(new JobQuery(), Ct)).Jobs);
        Assert.Equal(OrchestratorFixture.Start.AddHours(5), job.ScheduledFor);
        Assert.Equal(OrchestratorFixture.Start.AddHours(6), (await f.Store.GetScheduleAsync(schedule.ScheduleId, Ct))!.NextFireAt);
    }

    [Fact]
    public async Task MissedFireTimes_Skip_CreatesNoJobAndSaysWhy()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        var schedule = await f.Orchestrator.CreateScheduleAsync(OrchestratorFixture.Admin, process.ProcessId, Hourly(MisfirePolicy.Skip), Ct);

        f.Time.Advance(TimeSpan.FromHours(3).Add(TimeSpan.FromMinutes(10)));
        Assert.Equal(0, await f.Orchestrator.EvaluateSchedulesAsync(Ct));

        var stored = (await f.Store.GetScheduleAsync(schedule.ScheduleId, Ct))!;
        Assert.StartsWith("Skipped", stored.LastOutcome, StringComparison.Ordinal);
        Assert.Equal(OrchestratorFixture.Start.AddHours(4), stored.NextFireAt);
    }

    [Fact]
    public async Task Overlap_Skip_SkipsWhileItsJobIsActive_Allow_DoesNot()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        await f.Orchestrator.CreateScheduleAsync(OrchestratorFixture.Admin, process.ProcessId, Hourly(overlap: OverlapPolicy.Skip) with { Name = "skip" }, Ct);
        await f.Orchestrator.CreateScheduleAsync(OrchestratorFixture.Admin, process.ProcessId, Hourly(overlap: OverlapPolicy.Allow) with { Name = "allow" }, Ct);

        f.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(2, await f.Orchestrator.EvaluateSchedulesAsync(Ct));
        f.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await f.Orchestrator.EvaluateSchedulesAsync(Ct));
    }

    [Fact]
    public async Task DisabledSchedule_DoesNotFire_AndEnablingDoesNotCatchUp()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        var schedule = await f.Orchestrator.CreateScheduleAsync(OrchestratorFixture.Admin, process.ProcessId, Hourly(), Ct);
        await f.Orchestrator.SetScheduleEnabledAsync(OrchestratorFixture.Admin, schedule.ScheduleId, false, Ct);

        f.Time.Advance(TimeSpan.FromHours(3).Add(TimeSpan.FromMinutes(30)));
        Assert.Equal(0, await f.Orchestrator.EvaluateSchedulesAsync(Ct));
        var enabled = await f.Orchestrator.SetScheduleEnabledAsync(OrchestratorFixture.Admin, schedule.ScheduleId, true, Ct);

        Assert.Equal(OrchestratorFixture.Start.AddHours(4), enabled.NextFireAt);
        Assert.Equal(0, await f.Orchestrator.EvaluateSchedulesAsync(Ct));
    }

    [Fact]
    public async Task DisabledDeployment_SkipsTheFireTime_WithAReason()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        var schedule = await f.Orchestrator.CreateScheduleAsync(OrchestratorFixture.Admin, process.ProcessId, Hourly(), Ct);
        await f.Orchestrator.SetEnabledAsync(OrchestratorFixture.Admin, process.ProcessId, false, Ct);

        f.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await f.Orchestrator.EvaluateSchedulesAsync(Ct));

        Assert.Equal("Skipped: the deployment is disabled.", (await f.Store.GetScheduleAsync(schedule.ScheduleId, Ct))!.LastOutcome);
    }

    [Fact]
    public async Task OnceSchedule_FiresOnce_ThenHasNoNextTime()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        var schedule = await f.Orchestrator.CreateScheduleAsync(OrchestratorFixture.Admin, process.ProcessId,
            new ScheduleDefinition { Name = "once", Kind = ScheduleKind.Once, StartAt = OrchestratorFixture.Start.AddMinutes(10), TimeZone = "UTC" }, Ct);

        f.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, await f.Orchestrator.EvaluateSchedulesAsync(Ct));
        f.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await f.Orchestrator.EvaluateSchedulesAsync(Ct));

        Assert.Null((await f.Store.GetScheduleAsync(schedule.ScheduleId, Ct))!.NextFireAt);
    }

    [Fact]
    public async Task InvalidSchedule_IsRefused()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());

        await Assert.ThrowsAsync<OrchestrationException>(() => f.Orchestrator.CreateScheduleAsync(OrchestratorFixture.Admin, process.ProcessId, Hourly() with { Cron = "61 * * * *" }, Ct));
        await Assert.ThrowsAsync<OrchestrationException>(() => f.Orchestrator.CreateScheduleAsync(OrchestratorFixture.Admin, process.ProcessId, Hourly() with { TimeZone = "Nowhere/Land" }, Ct));
        await Assert.ThrowsAsync<OrchestrationException>(() => f.Orchestrator.SetScheduleEnabledAsync(new Actor("token:v", Role.Viewer), "sch_x", false, Ct));
    }
}

public sealed class SqliteStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UpdateJob_WithAStaleRevision_Fails()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        var job = await f.SubmitAsync(process.ProcessId);

        Assert.True(await f.Store.UpdateJobAsync(job with { Priority = 5 }, Ct));
        Assert.False(await f.Store.UpdateJobAsync(job with { Priority = 9 }, Ct));
        var stored = await f.JobAsync(job.JobId);
        Assert.Equal((5, 1L), (stored.Priority, stored.Revision));
    }

    [Fact]
    public async Task PublishedPackages_CannotBeChangedOrDeleted_EvenWithSql()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var (process, package) = await f.PublishAsync(Workflows.Hello());

        Assert.False(await f.Store.InsertPackageAsync(package, new byte[] { 1 }, Ct));
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(f.Folder, "orchestration.db")};Pooling=False");
        await connection.OpenAsync(Ct);
        foreach (var sql in new[] { "UPDATE packages SET content = x'00'", "DELETE FROM packages" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var ex = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync(Ct));
            Assert.Contains("immutable", ex.Message, StringComparison.Ordinal);
        }

        var content = await f.Store.ReadPackageContentAsync(process.ProcessId, 1, Ct);
        Assert.True(WorkflowPackage.TryVerify(content, package.Sha256, out _, out _));
    }

    [Fact]
    public async Task AppendEvents_IgnoresEventsItAlreadyHas()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        static StoredEvent Event(int attempt, long sequence) => new(attempt, sequence, "log", OrchestratorFixture.Start, "{}");

        Assert.Equal(2, await f.Store.AppendEventsAsync("job_x", [Event(1, 1), Event(1, 2)], Ct));
        Assert.Equal(1, await f.Store.AppendEventsAsync("job_x", [Event(1, 2), Event(2, 1)], Ct));

        var events = await f.Store.ReadEventsAsync("job_x", 1, 1, 100, Ct);
        Assert.Equal([(1, 2L), (2, 1L)], events.Select(e => (e.Attempt, e.Sequence)));
    }

    [Fact]
    public async Task QueryJobs_FiltersPagesAndCounts()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        for (var i = 0; i < 5; i++)
        {
            f.Time.Advance(TimeSpan.FromMinutes(1));
            await f.SubmitAsync(process.ProcessId);
        }

        var cancelled = (await f.Store.QueryJobsAsync(new JobQuery { Limit = 1 }, Ct)).Jobs[0];
        await f.Orchestrator.CancelAsync(OrchestratorFixture.Admin, cancelled.JobId, Ct);

        var queued = await f.Store.QueryJobsAsync(new JobQuery { States = [JobState.Queued], Offset = 1, Limit = 2 }, Ct);
        Assert.Equal(4, queued.Total);
        Assert.Equal(2, queued.Jobs.Count);
        Assert.True(queued.Jobs[0].CreatedAt > queued.Jobs[1].CreatedAt);
        var stats = await f.Store.GetStatsAsync(OrchestratorFixture.Start, TimeSpan.FromHours(1), Ct);
        Assert.Equal((4, 1), (stats.ByState[JobState.Queued], stats.ByState[JobState.Cancelled]));
        Assert.Equal(1, Assert.Single(stats.Buckets).Cancelled);
    }

    [Fact]
    public async Task Reopen_KeepsEverything_AndInitializeIsIdempotent()
    {
        var folder = Path.Combine(Path.GetTempPath(), "myrpa-orch-" + Path.GetRandomFileName());
        try
        {
            await using (var before = await OrchestratorFixture.CreateAsync(folder))
            {
                await before.DeployedAsync(Workflows.Hello());
                await before.Orchestrator.SetPolicyAsync(OrchestratorFixture.Admin, new OrchestrationPolicy { MaxAttempts = 5 }, Ct);
            }

            await using var store = new SqliteOrchestrationStore(Path.Combine(folder, "orchestration.db"));
            await store.InitializeAsync(Ct);
            await store.InitializeAsync(Ct);
            Assert.Single(await store.ListProcessesAsync(Ct));
            Assert.Equal(5, (await store.GetPolicyAsync(Ct)).MaxAttempts);
            var diagnostics = await store.GetDiagnosticsAsync(Ct);
            Assert.Equal((SqliteOrchestrationStore.SchemaVersion, 1L), (diagnostics.SchemaVersion, diagnostics.Counts["packages"]));
        }
        finally
        {
            OrchestratorFixture.TryDelete(folder);
        }
    }
}
