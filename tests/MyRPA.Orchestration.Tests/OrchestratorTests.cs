using MyRPA.Contracts.Execution;
using MyRPA.Contracts.Orchestration;

namespace MyRPA.Orchestration.Tests;

public sealed class OrchestratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Publish_EachPublishAddsAnImmutableVersion()
    {
        await using var f = await OrchestratorFixture.CreateAsync();

        var (process, first) = await f.PublishAsync(Workflows.Hello());
        var (_, second) = await f.PublishAsync(Workflows.Hello(name: "Hello again"));

        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.NotEqual(first.Sha256, second.Sha256);
        var stored = await f.Store.GetPackageAsync(process.ProcessId, 1, Ct);
        Assert.Equal(first.Sha256, stored!.Sha256);
        Assert.Equal(2, (await f.Store.GetProcessAsync(process.ProcessId, Ct))!.LatestVersion);
    }

    [Fact]
    public async Task Publish_InvalidEntry_IsRefusedAndStoresNothing()
    {
        await using var f = await OrchestratorFixture.CreateAsync();

        var ex = await Assert.ThrowsAsync<OrchestrationException>(() => f.PublishAsync("{\"schemaVersion\":\"1.0\"}"));

        Assert.Equal(OrchestrationFailure.Invalid, ex.Failure);
        Assert.Empty(await f.Store.ListProcessesAsync(Ct));
    }

    [Fact]
    public async Task Deploy_ThenRollback_KeepsEveryVersionAndAuditsBoth()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var (process, _) = await f.PublishAsync(Workflows.Hello());
        await f.PublishAsync(Workflows.Hello(name: "v2"));

        await f.Orchestrator.DeployAsync(OrchestratorFixture.Admin, process.ProcessId, 2, Ct);
        var rolledBack = await f.Orchestrator.DeployAsync(OrchestratorFixture.Admin, process.ProcessId, 1, Ct);

        Assert.Equal(1, rolledBack.DeployedVersion);
        Assert.Equal(2, (await f.Store.ListPackagesAsync(process.ProcessId, Ct)).Count);
        var audit = await f.Store.ListAuditAsync(0, 10, Ct);
        Assert.Contains(audit, a => a.Action == "process.rollback" && a.Details == "v1 (was v2)");
        Assert.Contains(audit, a => a.Action == "process.deploy");
    }

    [Fact]
    public async Task Submit_PinsTheDeployedVersion_ALaterDeploymentDoesNotChangeIt()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        var job = await f.SubmitAsync(process.ProcessId);

        var (_, v2) = await f.PublishAsync(Workflows.Hello(name: "v2"));
        await f.Orchestrator.DeployAsync(OrchestratorFixture.Admin, process.ProcessId, v2.Version, Ct);

        var stored = await f.JobAsync(job.JobId);
        Assert.Equal((1, JobState.Queued), (stored.Version, stored.State));
        var runner = await f.RunnerAsync("r1");
        var assignment = await f.ClaimAsync(runner);
        Assert.Equal(1, assignment!.Version);
        Assert.Equal(stored.PackageSha256, assignment.PackageSha256);
    }

    [Fact]
    public async Task Submit_WithoutADeployment_NeedsAnExplicitVersion()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var (process, _) = await f.PublishAsync(Workflows.Hello());

        var ex = await Assert.ThrowsAsync<OrchestrationException>(() => f.SubmitAsync(process.ProcessId));
        var explicitJob = await f.SubmitAsync(process.ProcessId, version: 1);

        Assert.Equal(OrchestrationFailure.Conflict, ex.Failure);
        Assert.Equal(1, explicitJob.Version);
    }

    [Fact]
    public async Task Submit_TheSameIdempotencyKeyTwice_ReturnsTheFirstJob()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());

        var (first, created1) = await f.Orchestrator.SubmitAsync(OrchestratorFixture.Admin, new SubmitJobRequest { ProcessId = process.ProcessId, IdempotencyKey = "k-1" }, Ct);
        var (second, created2) = await f.Orchestrator.SubmitAsync(OrchestratorFixture.Admin, new SubmitJobRequest { ProcessId = process.ProcessId, IdempotencyKey = "k-1" }, Ct);

        Assert.True(created1);
        Assert.False(created2);
        Assert.Equal(first.JobId, second.JobId);
        Assert.Equal(1, (await f.Store.QueryJobsAsync(new JobQuery(), Ct)).Total);
    }

    [Fact]
    public async Task Submit_ValidatesArgumentsAgainstTheExactVersion()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var hello = await f.DeployedAsync(Workflows.Hello());

        async Task<OrchestrationFailure> Fail(IReadOnlyDictionary<string, string> text) =>
            (await Assert.ThrowsAsync<OrchestrationException>(() => f.Orchestrator.SubmitAsync(OrchestratorFixture.Admin, new SubmitJobRequest { ProcessId = hello.ProcessId, ArgumentText = text }, Ct))).Failure;

        Assert.Equal(OrchestrationFailure.Invalid, await Fail(new Dictionary<string, string> { ["nobody"] = "x" }));
        Assert.Equal(OrchestrationFailure.Invalid, await Fail(new Dictionary<string, string> { ["count"] = "many" }));
        Assert.Equal(OrchestrationFailure.Invalid, await Fail(new Dictionary<string, string> { ["greeting"] = "out arguments are not inputs" }));

        var (job, _) = await f.Orchestrator.SubmitAsync(OrchestratorFixture.Admin, new SubmitJobRequest { ProcessId = hello.ProcessId, ArgumentText = new Dictionary<string, string> { ["who"] = "Ada", ["count"] = "3" } }, Ct);
        Assert.Equal("{\"count\":3,\"who\":\"Ada\"}", job.ArgumentsJson);
    }

    [Fact]
    public async Task Submit_MissingRequiredArgument_IsRefused()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Required());

        var ex = await Assert.ThrowsAsync<OrchestrationException>(() => f.SubmitAsync(process.ProcessId));

        Assert.Contains("customer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submit_ASecretFedArgument_IsNeverStoredWithAJob()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.WithSecret());

        var ex = await Assert.ThrowsAsync<OrchestrationException>(() => f.Orchestrator.SubmitAsync(OrchestratorFixture.Admin,
            new SubmitJobRequest { ProcessId = process.ProcessId, ArgumentText = new Dictionary<string, string> { ["password"] = "hunter2" } }, Ct));

        Assert.Contains("secret", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, (await f.Store.QueryJobsAsync(new JobQuery(), Ct)).Total);
    }

    [Fact]
    public async Task Submit_ToADisabledDeployment_IsRefused()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        await f.Orchestrator.SetEnabledAsync(OrchestratorFixture.Admin, process.ProcessId, false, Ct);

        var ex = await Assert.ThrowsAsync<OrchestrationException>(() => f.SubmitAsync(process.ProcessId));

        Assert.Equal(OrchestrationFailure.Conflict, ex.Failure);
    }

    [Fact]
    public async Task Claim_OnlyAnEligibleRunnerGetsTheJob_AndTheOthersAreExplained()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.WithPlugin());
        var job = await f.SubmitAsync(process.ProcessId);
        var bare = await f.RunnerAsync("bare", activities: TestCatalog.BuiltInActivities, plugins: []);
        var old = await f.RunnerAsync("old", plugins: [new RunnerPluginInfo("Demo.Plugin", "1.0.0")]);
        var full = await f.RunnerAsync("full");

        Assert.Null(await f.ClaimAsync(bare));
        Assert.Null(await f.ClaimAsync(old));
        var reasons = await f.Orchestrator.ExplainWaitAsync(await f.JobAsync(job.JobId), Ct);
        Assert.Contains(reasons, r => r.StartsWith("bare: lacks Demo.Thing", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("needs 1.2.0 or later", StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.StartsWith("full: eligible", StringComparison.Ordinal));

        Assert.Equal(job.JobId, (await f.ClaimAsync(full))!.JobId);
    }

    [Fact]
    public async Task Claim_RespectsTargetGroupTargetRunnerAndCapacity()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        await f.Orchestrator.ConfigureAsync(OrchestratorFixture.Admin, process.ProcessId, "finance", null, 0, null, Ct);
        await f.SubmitAsync(process.ProcessId);
        await f.SubmitAsync(process.ProcessId);
        var ops = await f.RunnerAsync("ops", group: "operations");
        var finance = await f.RunnerAsync("fin", group: "finance", capacity: 1);

        Assert.Null(await f.ClaimAsync(ops));
        Assert.NotNull(await f.ClaimAsync(finance));
        Assert.Null(await f.ClaimAsync(finance));
    }

    [Fact]
    public async Task Claim_ManyRunnersAtOnce_ExactlyOneGetsTheJob()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        await f.SubmitAsync(process.ProcessId);
        var runners = new List<RunnerRecord>();
        for (var i = 0; i < 8; i++)
        {
            runners.Add(await f.RunnerAsync($"r{i}"));
        }

        var claims = await Task.WhenAll(runners.Select(r => Task.Run(() => f.ClaimAsync(r), Ct)));

        Assert.Single(claims, c => c is not null);
        var job = (await f.Store.QueryJobsAsync(new JobQuery(), Ct)).Jobs.Single();
        Assert.Equal((JobState.Assigned, 1), (job.State, job.AttemptCount));
    }

    [Fact]
    public async Task Lifecycle_StartEventsComplete_IsPersistedAndDuplicatesChangeNothing()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        await f.SubmitAsync(process.ProcessId);
        var runner = await f.RunnerAsync("r1");
        var a = (await f.ClaimAsync(runner))!;

        var start = await f.Orchestrator.StartAsync(runner, a.JobId, new JobStartRequest(a.LeaseId, "run-1"), Ct);
        var events = new JobEventsRequest { LeaseId = a.LeaseId, Events = [Event(1, "execution.started"), Event(2, "log", "hello")] };
        var first = await f.Orchestrator.AppendEventsAsync(runner, a.JobId, events, Ct);
        var again = await f.Orchestrator.AppendEventsAsync(runner, a.JobId, events, Ct);
        var complete = new JobCompleteRequest { LeaseId = a.LeaseId, Status = "Succeeded", OutputsJson = "{\"greeting\":\"Hello, World\"}", DurationMs = 12 };
        var done = await f.Orchestrator.CompleteAsync(runner, a.JobId, complete, Ct);
        var duplicate = await f.Orchestrator.CompleteAsync(runner, a.JobId, complete with { Status = "Failed" }, Ct);

        Assert.True(start.LeaseValid);
        Assert.False(first.Duplicate);
        Assert.True(again.Duplicate);
        Assert.Equal("Succeeded", done.State);
        Assert.True(duplicate.Duplicate);
        var job = await f.JobAsync(a.JobId);
        Assert.Equal((JobState.Succeeded, 2), (job.State, job.EventCount));
        Assert.Equal("{\"greeting\":\"Hello, World\"}", job.OutputsJson);
        Assert.Equal("Succeeded", Assert.Single(job.Attempts).Outcome);
        Assert.Equal(2, (await f.Store.ReadEventsAsync(a.JobId, 0, 0, 100, Ct)).Count);
    }

    [Fact]
    public async Task Complete_WithOutputsThatAreNotAJsonObject_StoresAMarkerInstead()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        await f.SubmitAsync(process.ProcessId);
        var runner = await f.RunnerAsync("r1");
        var a = (await f.ClaimAsync(runner))!;
        await f.Orchestrator.StartAsync(runner, a.JobId, new JobStartRequest(a.LeaseId, null), Ct);

        await f.Orchestrator.CompleteAsync(runner, a.JobId, new JobCompleteRequest { LeaseId = a.LeaseId, Status = "Succeeded", OutputsJson = "not json" }, Ct);

        Assert.Contains("_invalid", (await f.JobAsync(a.JobId)).OutputsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Messages_FromAnotherRunnerOrAStaleLease_ChangeNothing()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        await f.SubmitAsync(process.ProcessId);
        var owner = await f.RunnerAsync("owner");
        var other = await f.RunnerAsync("other");
        var a = (await f.ClaimAsync(owner))!;

        var foreign = await f.Orchestrator.CompleteAsync(other, a.JobId, new JobCompleteRequest { LeaseId = a.LeaseId, Status = "Succeeded" }, Ct);
        var stale = await f.Orchestrator.StartAsync(owner, a.JobId, new JobStartRequest("lse_stale", null), Ct);

        Assert.False(foreign.LeaseValid);
        Assert.Null(foreign.State);
        Assert.False(stale.LeaseValid);
        Assert.Equal(JobState.Assigned, (await f.JobAsync(a.JobId)).State);
        Assert.Null(await f.Orchestrator.FindLeasedJobAsync(other, a.JobId, a.LeaseId, Ct));
    }

    [Fact]
    public async Task Cancel_QueuedJob_IsCancelledAtOnce_AndCannotBeCancelledAgain()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        var job = await f.SubmitAsync(process.ProcessId);

        var cancelled = await f.Orchestrator.CancelAsync(OrchestratorFixture.Admin, job.JobId, Ct);
        var again = await Assert.ThrowsAsync<OrchestrationException>(() => f.Orchestrator.CancelAsync(OrchestratorFixture.Admin, job.JobId, Ct));

        Assert.Equal(JobState.Cancelled, cancelled.State);
        Assert.Equal(OrchestrationFailure.Conflict, again.Failure);
    }

    [Fact]
    public async Task Cancel_RunningJob_ReachesTheRunner_AndIsFinalOnlyWhenItReports()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        await f.SubmitAsync(process.ProcessId);
        var runner = await f.RunnerAsync("r1");
        var a = (await f.ClaimAsync(runner))!;
        await f.Orchestrator.StartAsync(runner, a.JobId, new JobStartRequest(a.LeaseId, null), Ct);

        var requested = await f.Orchestrator.CancelAsync(OrchestratorFixture.Admin, a.JobId, Ct);
        var heartbeat = await f.Orchestrator.HeartbeatAsync(runner, new RunnerHeartbeatRequest { ActiveJobs = [new(a.JobId, a.LeaseId)] }, Ct);
        await f.Orchestrator.CompleteAsync(runner, a.JobId, new JobCompleteRequest { LeaseId = a.LeaseId, Status = "Cancelled", Error = new ExecutionErrorMessage("MYRPA2006", "The run was cancelled.") }, Ct);

        Assert.Equal((JobState.Running, true), (requested.State, requested.CancelRequested));
        Assert.Equal([a.JobId], heartbeat.Cancel);
        Assert.Equal(JobState.Cancelled, (await f.JobAsync(a.JobId)).State);
    }

    [Fact]
    public async Task Cancel_AssignedJob_TheRunnerIsToldNotToStart()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        await f.SubmitAsync(process.ProcessId);
        var runner = await f.RunnerAsync("r1");
        var a = (await f.ClaimAsync(runner))!;

        await f.Orchestrator.CancelAsync(OrchestratorFixture.Admin, a.JobId, Ct);
        var start = await f.Orchestrator.StartAsync(runner, a.JobId, new JobStartRequest(a.LeaseId, null), Ct);

        Assert.False(start.LeaseValid);
        Assert.True(start.CancelRequested);
        Assert.Equal(JobState.Cancelled, (await f.JobAsync(a.JobId)).State);
    }

    [Fact]
    public async Task LeaseExpiry_BeforeStart_Requeues_ThenFailsAfterMaxAttempts()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        var job = await f.SubmitAsync(process.ProcessId);
        var runner = await f.RunnerAsync("r1");

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.NotNull(await f.ClaimAsync(runner));
            f.Time.Advance(TimeSpan.FromSeconds(31));
            Assert.Equal(1, await f.Orchestrator.SweepAsync(Ct));
        }

        var failed = await f.JobAsync(job.JobId);
        Assert.Equal(JobState.Failed, failed.State);
        Assert.Equal(OrchestrationErrorCodes.NotStarted, failed.Error!.Code);
        Assert.Equal(["Expired", "Expired", "Expired"], failed.Attempts.Select(a => a.Outcome));
    }

    [Fact]
    public async Task LeaseExpiry_WhileRunning_Interrupts_IsNeverRequeued_AndALateCompletionSettlesIt()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        await f.SubmitAsync(process.ProcessId);
        var runner = await f.RunnerAsync("r1");
        var other = await f.RunnerAsync("r2");
        var a = (await f.ClaimAsync(runner))!;
        await f.Orchestrator.StartAsync(runner, a.JobId, new JobStartRequest(a.LeaseId, null), Ct);

        f.Time.Advance(TimeSpan.FromSeconds(31));
        await f.Orchestrator.SweepAsync(Ct);
        var interrupted = await f.JobAsync(a.JobId);
        Assert.Equal(JobState.Interrupted, interrupted.State);
        Assert.Equal(OrchestrationErrorCodes.Interrupted, interrupted.Error!.Code);
        Assert.Null(await f.ClaimAsync(other));
        await Assert.ThrowsAsync<OrchestrationException>(() => f.Orchestrator.CancelAsync(OrchestratorFixture.Admin, a.JobId, Ct));

        var late = await f.Orchestrator.CompleteAsync(runner, a.JobId, new JobCompleteRequest { LeaseId = a.LeaseId, Status = "Succeeded" }, Ct);
        Assert.True(late.LeaseValid);
        Assert.Equal(JobState.Succeeded, (await f.JobAsync(a.JobId)).State);
    }

    [Fact]
    public async Task RunnerRestart_RequeuesNotStartedJobs_AndInterruptsRunningOnes()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        await f.SubmitAsync(process.ProcessId);
        await f.SubmitAsync(process.ProcessId);
        var runner = await f.RunnerAsync("r1", capacity: 2);
        var running = (await f.ClaimAsync(runner))!;
        var assigned = (await f.ClaimAsync(runner))!;
        await f.Orchestrator.StartAsync(runner, running.JobId, new JobStartRequest(running.LeaseId, null), Ct);

        await f.RegisterAsync(runner, capacity: 2);

        Assert.Equal(JobState.Interrupted, (await f.JobAsync(running.JobId)).State);
        Assert.Equal(JobState.Queued, (await f.JobAsync(assigned.JobId)).State);
    }

    [Fact]
    public async Task Release_RequeuesWithoutUsingUpAnAttempt()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        await f.SubmitAsync(process.ProcessId);
        var runner = await f.RunnerAsync("r1");
        var a = (await f.ClaimAsync(runner))!;

        await f.Orchestrator.ReleaseAsync(runner, a.JobId, new JobReleaseRequest(a.LeaseId, "shutting down"), Ct);

        var job = await f.JobAsync(a.JobId);
        Assert.Equal(JobState.Queued, job.State);
        Assert.Equal("Released", job.Attempts.Single().Outcome);
        Assert.NotNull(await f.ClaimAsync(runner));
    }

    [Fact]
    public async Task Retry_CreatesALinkedJobForTheSameVersion_OnlyForFinishedJobs()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        var original = await f.SubmitAsync(process.ProcessId);

        await Assert.ThrowsAsync<OrchestrationException>(() => f.Orchestrator.RetryAsync(OrchestratorFixture.Admin, original.JobId, null, Ct));
        await f.Orchestrator.CancelAsync(OrchestratorFixture.Admin, original.JobId, Ct);
        var (retry, created) = await f.Orchestrator.RetryAsync(OrchestratorFixture.Admin, original.JobId, null, Ct);

        Assert.True(created);
        Assert.Equal((original.JobId, "retry", original.Version, JobState.Queued), (retry.RetryOfJobId, retry.Source, retry.Version, retry.State));
    }

    [Fact]
    public async Task Roles_AreEnforcedOnEveryChange()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        var viewer = new Actor("token:viewer", Role.Viewer);
        var operatorActor = new Actor("token:operator", Role.Operator);
        var developer = new Actor("token:developer", Role.Developer);

        async Task Forbidden(Func<Task> action) => Assert.Equal(OrchestrationFailure.Forbidden, (await Assert.ThrowsAsync<OrchestrationException>(action)).Failure);

        await Forbidden(() => f.Orchestrator.SubmitAsync(viewer, new SubmitJobRequest { ProcessId = process.ProcessId }, Ct));
        await Forbidden(() => f.Orchestrator.PublishAsync(operatorActor, "p", ["main.json"], [new("main.json", Workflows.Hello())], null, TestCatalog.PluginOf, Ct));
        await Forbidden(() => f.Orchestrator.DeployAsync(operatorActor, process.ProcessId, 1, Ct));
        await Forbidden(() => f.Orchestrator.CreateRunnerAsync(developer, "r", null, 1, Ct));
        await Forbidden(() => f.Orchestrator.CreateAccessTokenAsync(developer, "t", Role.Admin, Ct));
        await Forbidden(() => f.Orchestrator.ListAuditAsync(developer, 0, 10, Ct));

        var (job, _) = await f.Orchestrator.SubmitAsync(operatorActor, new SubmitJobRequest { ProcessId = process.ProcessId }, Ct);
        Assert.Equal("token:operator", job.SubmittedBy);
    }

    [Fact]
    public async Task AccessTokens_Authenticate_UntilRevoked()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var (record, token) = await f.Orchestrator.CreateAccessTokenAsync(OrchestratorFixture.Admin, "ci", Role.Operator, Ct);

        var actor = await f.Orchestrator.AuthenticateAccessTokenAsync(token, Ct);
        var forged = await f.Orchestrator.AuthenticateAccessTokenAsync(token[..^4] + "AAAA", Ct);
        await f.Orchestrator.RevokeAccessTokenAsync(OrchestratorFixture.Admin, record.TokenId, Ct);
        var revoked = await f.Orchestrator.AuthenticateAccessTokenAsync(token, Ct);

        Assert.Equal(new Actor("token:ci", Role.Operator), actor);
        Assert.Null(forged);
        Assert.Null(revoked);
        Assert.DoesNotContain(token.Split('.')[1], (await f.Store.GetAccessTokenAsync(record.TokenId, Ct))!.Hash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerTokens_StopWorkingWhenRevokedOrRotated()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var (runner, token) = await f.Orchestrator.CreateRunnerAsync(OrchestratorFixture.Admin, "r1", null, 1, Ct);

        Assert.NotNull(await f.Orchestrator.AuthenticateRunnerAsync(token, Ct));
        var rotated = await f.Orchestrator.RotateRunnerTokenAsync(OrchestratorFixture.Admin, runner.RunnerId, Ct);
        Assert.Null(await f.Orchestrator.AuthenticateRunnerAsync(token, Ct));
        Assert.NotNull(await f.Orchestrator.AuthenticateRunnerAsync(rotated, Ct));
        await f.Orchestrator.RevokeRunnerAsync(OrchestratorFixture.Admin, runner.RunnerId, Ct);
        Assert.Null(await f.Orchestrator.AuthenticateRunnerAsync(rotated, Ct));
    }

    [Fact]
    public async Task RunnerStatus_FollowsHeartbeatsPauseAndLoad()
    {
        await using var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        await f.SubmitAsync(process.ProcessId);
        var runner = await f.RunnerAsync("r1");

        Assert.Equal(RunnerStatus.Available, Single(await f.Orchestrator.ListRunnersAsync(Ct)));
        await f.ClaimAsync(runner);
        Assert.Equal(RunnerStatus.Busy, Single(await f.Orchestrator.ListRunnersAsync(Ct)));
        await f.Orchestrator.UpdateRunnerAsync(OrchestratorFixture.Admin, runner.RunnerId, null, null, true, Ct);
        Assert.Equal(RunnerStatus.Paused, Single(await f.Orchestrator.ListRunnersAsync(Ct)));
        f.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(RunnerStatus.Offline, Single(await f.Orchestrator.ListRunnersAsync(Ct)));

        static RunnerStatus Single(IReadOnlyList<RunnerView> views) => Assert.Single(views).Status;
    }

    [Fact]
    public async Task ServerRestart_KeepsAcceptedJobs_AndTheyAreStillDispatched()
    {
        var folder = Path.Combine(Path.GetTempPath(), "myrpa-orch-" + Path.GetRandomFileName());
        try
        {
            string jobId;
            await using (var before = await OrchestratorFixture.CreateAsync(folder))
            {
                var process = await before.DeployedAsync(Workflows.Hello());
                jobId = (await before.SubmitAsync(process.ProcessId)).JobId;
            }

            await using var after = await OrchestratorFixture.CreateAsync(folder, ids: new PrefixIds("b"));
            var runner = await after.RunnerAsync("r1");
            Assert.Equal(JobState.Queued, (await after.JobAsync(jobId)).State);
            Assert.Equal(jobId, (await after.ClaimAsync(runner))!.JobId);
        }
        finally
        {
            OrchestratorFixture.TryDelete(folder);
        }
    }

    [Fact]
    public async Task PersistenceFailure_SubmissionIsNotReportedAsAccepted()
    {
        var f = await OrchestratorFixture.CreateAsync();
        var process = await f.DeployedAsync(Workflows.Hello());
        await f.Store.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => f.SubmitAsync(process.ProcessId));

        await f.DisposeAsync();
    }

    private static ExecutionEventMessage Event(long sequence, string kind, string? message = null) =>
        new() { Sequence = sequence, Kind = kind, RunId = "run-1", Time = OrchestratorFixture.Start, Message = message, Level = message is null ? null : "Information" };

    private sealed class PrefixIds(string prefix) : IOrchestrationIds
    {
        private int _next;

        public string NewId(string kind) => $"{kind}_{prefix}{Interlocked.Increment(ref _next):D4}";
    }
}
