using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyRPA.Contracts.Orchestration;

namespace MyRPA.Runner.Tests;

/// <summary>Phase 10 acceptance: publish → submit → dispatch → Runner executes with the engine → history on the Server.</summary>
public sealed class RunnerEndToEndTests
{
    private static readonly string[] _twoEntryPoints = ["greet.json", "farewell/bye.json"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PublishSubmitDispatch_RunsOnTheRunner_AndTheServerKeepsTheHistory()
    {
        await using var stack = await Stack.StartAsync();
        stack.Write("greet.json", Flows.Greet);
        var processId = await stack.DeployAsync("greet.json");
        var (runnerId, token) = await stack.CreateRunnerAsync("finance-1");
        await stack.StartRunnerAsync(token);

        var jobId = await stack.SubmitAsync(processId, new { who = "Ada" });
        var detail = await stack.WaitForJobAsync(jobId, "Succeeded", "Failed");

        var job = detail.GetProperty("job");
        Assert.Equal("Succeeded", job.GetProperty("state").GetString());
        Assert.Equal((runnerId, "finance-1"), (job.GetProperty("runnerId").GetString(), job.GetProperty("runnerName").GetString()));
        Assert.Equal(1, job.GetProperty("version").GetInt32());
        Assert.Equal("Hello, Ada", detail.GetProperty("outputs").GetProperty("greeting").GetString());
        var events = (await Stack.GetAsync(stack.Admin, $"/api/orchestrator/jobs/{jobId}/events")).GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("event")).ToList();
        Assert.Equal("execution.started", events[0].GetProperty("kind").GetString());
        Assert.Contains(events, e => e.GetProperty("kind").GetString() == "log" && e.GetProperty("message").GetString() == "Hello, Ada");
        Assert.Equal("execution.completed", events[^1].GetProperty("kind").GetString());
        Assert.All(events, e => Assert.Equal(jobId, e.GetProperty("runId").GetString()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(stack.Folder, "work-runner", "jobs")));
    }

    [Fact]
    public async Task ProjectWithTwoEntryPoints_RunsTheNamedEntryPoint()
    {
        await using var stack = await Stack.StartAsync();
        stack.Write("greet.json", Flows.Greet);
        stack.Write("farewell/bye.json", Flows.Greet.Replace("\"greet\"", "\"bye\"", StringComparison.Ordinal).Replace("'Hello, '", "'Bye, '", StringComparison.Ordinal));
        var published = await Stack.PostAsync(stack.Admin, "/api/orchestrator/publish", new { project = "project", entryPoints = _twoEntryPoints });
        var processId = published.GetProperty("process").GetProperty("processId").GetString()!;
        Assert.Equal("project", published.GetProperty("process").GetProperty("name").GetString());
        var entryPoints = published.GetProperty("version").GetProperty("entryPoints").EnumerateArray().ToList();
        Assert.Equal(["greet.json", "farewell/bye.json"], entryPoints.Select(e => e.GetProperty("path").GetString()));
        Assert.Equal("who", entryPoints[1].GetProperty("arguments")[0].GetProperty("name").GetString());
        await Stack.PostAsync(stack.Admin, $"/api/orchestrator/processes/{processId}/deploy", new { version = 1 });
        var (_, token) = await stack.CreateRunnerAsync("r1");
        await stack.StartRunnerAsync(token);

        var refused = await Stack.PostAsync(stack.Admin, "/api/orchestrator/jobs", new { processId }, 400);
        Assert.Contains("entry points", refused.GetProperty("error").GetString(), StringComparison.Ordinal);
        var submitted = await Stack.PostAsync(stack.Admin, "/api/orchestrator/jobs", new { processId, entryPath = "farewell/bye.json", argumentText = new { who = "Ada" } });
        var jobId = submitted.GetProperty("job").GetProperty("jobId").GetString()!;
        var detail = await stack.WaitForJobAsync(jobId, "Succeeded", "Failed");

        Assert.Equal("farewell/bye.json", detail.GetProperty("entryPath").GetString());
        Assert.Equal("Bye, Ada", detail.GetProperty("outputs").GetProperty("greeting").GetString());
    }

    [Fact]
    public async Task AgentWorkflow_RunsOnARunner_WithTheScriptedProvider_AndAgentEventsReachTheHistory()
    {
        var folder = Path.Combine(Path.GetTempPath(), "myrpa-stack-" + Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try
        {
            var config = Paths.AgentPluginConfig(folder);
            await using var stack = await Stack.StartAsync(config, folder);
            var samples = Path.Combine(Paths.Of("SamplesDirectory"), "plugins");
            stack.Write("agent-demo.json", File.ReadAllText(Path.Combine(samples, "agent-demo.json")));
            stack.Write("agent-tools/credit-limit.json", File.ReadAllText(Path.Combine(samples, "agent-tools", "credit-limit.json")));
            var processId = await stack.DeployAsync("agent-demo.json");
            var version = await Stack.GetAsync(stack.Admin, $"/api/orchestrator/processes/{processId}/versions/1");
            Assert.Contains(version.GetProperty("plugins").EnumerateArray(), p => p.GetProperty("id").GetString() == "MyRPA.Ai.Agent");
            Assert.Contains(version.GetProperty("files").EnumerateArray(), f => f.GetProperty("path").GetString() == "agent-tools/credit-limit.json");
            var (_, token) = await stack.CreateRunnerAsync("ai-runner");
            await stack.StartRunnerAsync(token, config);

            var jobId = await stack.SubmitAsync(processId);
            var detail = await stack.WaitForJobAsync(jobId, "Succeeded", "Failed");

            Assert.True(detail.GetProperty("job").GetProperty("state").GetString() == "Succeeded", detail.ToString());
            Assert.Equal("approve", detail.GetProperty("outputs").GetProperty("decision").GetString());
            var events = (await Stack.GetAsync(stack.Admin, $"/api/orchestrator/jobs/{jobId}/events")).GetProperty("events").EnumerateArray()
                .Select(e => e.GetProperty("event")).ToList();
            Assert.Contains(events, e => e.TryGetProperty("message", out var m) && m.GetString()!.StartsWith("AI.Agent:", StringComparison.Ordinal));
        }
        finally
        {
            Stack.TryDelete(folder);
        }
    }

    [Fact]
    public async Task MissingPlugin_TheJobWaits_AndTheServerSaysWhy()
    {
        var folder = Path.Combine(Path.GetTempPath(), "myrpa-stack-" + Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try
        {
            await using var stack = await Stack.StartAsync(Paths.AgentPluginConfig(folder), folder);
            var samples = Path.Combine(Paths.Of("SamplesDirectory"), "plugins");
            stack.Write("agent-demo.json", File.ReadAllText(Path.Combine(samples, "agent-demo.json")));
            stack.Write("agent-tools/credit-limit.json", File.ReadAllText(Path.Combine(samples, "agent-tools", "credit-limit.json")));
            var processId = await stack.DeployAsync("agent-demo.json");
            var (_, token) = await stack.CreateRunnerAsync("plain");
            await stack.StartRunnerAsync(token);

            var jobId = await stack.SubmitAsync(processId);
            await Task.Delay(1500, Ct);
            var detail = await stack.WaitForJobAsync(jobId, "Queued");

            Assert.Contains(detail.GetProperty("waiting").EnumerateArray(), w => w.GetString()!.StartsWith("plain: lacks AI.Agent", StringComparison.Ordinal));
        }
        finally
        {
            Stack.TryDelete(folder);
        }
    }

    [Fact]
    public async Task Cancel_ARunningJob_IsStoppedOnTheRunner()
    {
        await using var stack = await Stack.StartAsync();
        stack.Write("wait.json", Flows.Wait);
        var processId = await stack.DeployAsync("wait.json");
        var (_, token) = await stack.CreateRunnerAsync("r1");
        await stack.StartRunnerAsync(token);
        var jobId = await stack.SubmitAsync(processId);
        await stack.WaitForJobAsync(jobId, "Running");

        await Stack.PostAsync(stack.Admin, $"/api/orchestrator/jobs/{jobId}/cancel", new { }, 202);
        var detail = await stack.WaitForJobAsync(jobId, "Cancelled", "Failed", "Succeeded");

        Assert.Equal("Cancelled", detail.GetProperty("job").GetProperty("state").GetString());
        Assert.StartsWith("token:", detail.GetProperty("cancelRequestedBy").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerShutdown_WhileAJobRuns_ReportsItCancelledWithTheReason()
    {
        await using var stack = await Stack.StartAsync();
        stack.Write("wait.json", Flows.Wait);
        var processId = await stack.DeployAsync("wait.json");
        var (_, token) = await stack.CreateRunnerAsync("r1");
        await stack.StartRunnerAsync(token);
        var jobId = await stack.SubmitAsync(processId);
        await stack.WaitForJobAsync(jobId, "Running");

        await stack.StopRunnersAsync();
        var detail = await stack.WaitForJobAsync(jobId, "Cancelled", "Interrupted", "Failed");

        Assert.Equal("Cancelled", detail.GetProperty("job").GetProperty("state").GetString());
        Assert.Contains("Runner shut down", detail.GetProperty("job").GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokedRunner_StopsItself_WithExitCode3()
    {
        await using var stack = await Stack.StartAsync();
        var (runnerId, token) = await stack.CreateRunnerAsync("r1");
        var host = await stack.StartRunnerAsync(token);
        var stopping = new TaskCompletionSource();
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => stopping.TrySetResult());
        await WaitUntilAsync(async () => (await Stack.GetAsync(stack.Admin, $"/api/orchestrator/runners/{runnerId}")).GetProperty("runner").GetProperty("status").GetString() == "Available");

        await Stack.PostAsync(stack.Admin, $"/api/orchestrator/runners/{runnerId}/revoke", new { });
        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(3, host.Services.GetRequiredService<RunnerExitCode>().Value);
        var runner = (await Stack.GetAsync(stack.Admin, $"/api/orchestrator/runners/{runnerId}")).GetProperty("runner");
        Assert.Equal("Disabled", runner.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ServerRestart_AQueuedJobSurvives_AndRunsAfterwards()
    {
        var folder = Path.Combine(Path.GetTempPath(), "myrpa-stack-" + Path.GetRandomFileName());
        try
        {
            string jobId, token;
            await using (var before = await Stack.StartAsync(folder: folder))
            {
                before.Write("greet.json", Flows.Greet);
                var processId = await before.DeployAsync("greet.json");
                (_, token) = await before.CreateRunnerAsync("r1");
                jobId = await before.SubmitAsync(processId);
            }

            await using var after = await Stack.StartAsync(folder: folder);
            await after.StartRunnerAsync(token);
            var detail = await after.WaitForJobAsync(jobId, "Succeeded", "Failed");

            Assert.Equal("Succeeded", detail.GetProperty("job").GetProperty("state").GetString());
        }
        finally
        {
            Stack.TryDelete(folder);
        }
    }

    [Fact]
    public async Task OnceSchedule_CreatesAJob_ThatARunnerExecutes()
    {
        await using var stack = await Stack.StartAsync();
        stack.Write("greet.json", Flows.Greet);
        var processId = await stack.DeployAsync("greet.json");
        var (_, token) = await stack.CreateRunnerAsync("r1");
        await stack.StartRunnerAsync(token);

        var schedule = await Stack.PostAsync(stack.Admin, $"/api/orchestrator/processes/{processId}/schedules",
            new { name = "soon", kind = "Once", startAt = DateTimeOffset.UtcNow.AddSeconds(1), timeZone = "UTC", arguments = new { who = "Scheduler" } }, 201);
        var scheduleId = schedule.GetProperty("scheduleId").GetString();
        JsonElement jobs = default;
        await WaitUntilAsync(async () =>
        {
            jobs = await Stack.GetAsync(stack.Admin, $"/api/orchestrator/jobs?scheduleId={scheduleId}");
            return jobs.GetProperty("total").GetInt32() == 1;
        });
        var jobId = jobs.GetProperty("jobs")[0].GetProperty("jobId").GetString()!;
        var detail = await stack.WaitForJobAsync(jobId, "Succeeded", "Failed");

        Assert.Equal("schedule", detail.GetProperty("job").GetProperty("source").GetString());
        Assert.Equal("Hello, Scheduler", detail.GetProperty("outputs").GetProperty("greeting").GetString());
    }

    [Fact]
    public async Task RunnerProtocol_RefusesMissingWrongAndOldCredentials()
    {
        await using var stack = await Stack.StartAsync();
        var (_, token) = await stack.CreateRunnerAsync("r1");
        var runnerUri = stack.Server.RunnerUri!;

        async Task<HttpStatusCode> Heartbeat(string? bearer, string protocol = "1")
        {
            using var client = stack.Client(bearer, runnerUri);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/runner/v1/heartbeat") { Content = JsonContent.Create(new { activeJobs = Array.Empty<object>() }) };
            request.Headers.Add(RunnerProtocol.VersionHeader, protocol);
            using var response = await client.SendAsync(request, Ct);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Heartbeat(token));
        Assert.Equal(HttpStatusCode.Unauthorized, await Heartbeat(null));
        Assert.Equal(HttpStatusCode.Unauthorized, await Heartbeat(token[..^3] + "abc"));
        Assert.Equal(HttpStatusCode.BadRequest, await Heartbeat(token, "0"));

        // The Runner listener serves no Studio API, and a Runner token is not an access token.
        using var runnerClient = stack.Client(token, runnerUri);
        Assert.Equal(HttpStatusCode.NotFound, (await runnerClient.GetAsync("/api/projects", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await runnerClient.GetAsync("/api/orchestrator/jobs", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await runnerClient.GetAsync("/", Ct)).StatusCode);
    }

    [Fact]
    public async Task Packages_AreServedOnlyToTheRunnerHoldingTheLease()
    {
        await using var stack = await Stack.StartAsync();
        stack.Write("greet.json", Flows.Greet);
        var processId = await stack.DeployAsync("greet.json");
        var (holderId, holder) = await stack.CreateRunnerAsync("holder");
        var (_, other) = await stack.CreateRunnerAsync("other");
        var jobId = await stack.SubmitAsync(processId);
        var holderRecord = (await stack.Orchestrator.Store.GetRunnerAsync(holderId, Ct))!;
        await stack.Orchestrator.RegisterAsync(holderRecord, new RunnerRegisterRequest
        {
            ProtocolVersion = 1,
            RunnerVersion = "test",
            OperatingSystem = "test",
            Architecture = "X64",
            MaxConcurrency = 1,
            Activities = ["Core.Sequence", "Core.Assign", "Core.Log"],
        }, Ct);
        var assignment = (await stack.Orchestrator.ClaimAsync(holderRecord, TimeSpan.Zero, Ct))!;

        async Task<HttpStatusCode> Download(string bearer, string lease)
        {
            using var client = stack.Client(bearer, stack.Server.RunnerUri);
            client.DefaultRequestHeaders.Add(RunnerProtocol.VersionHeader, "1");
            using var response = await client.GetAsync($"/runner/v1/jobs/{jobId}/package?lease={lease}", Ct);
            return response.StatusCode;
        }

        Assert.Equal(jobId, assignment.JobId);
        Assert.Equal(HttpStatusCode.OK, await Download(holder, assignment.LeaseId));
        Assert.Equal(HttpStatusCode.NotFound, await Download(other, assignment.LeaseId));
        Assert.Equal(HttpStatusCode.NotFound, await Download(holder, "lse_wrong"));
    }

    [Fact]
    public async Task AccessTokens_EnforceRoles_HideJobData_AndStopWhenRevoked()
    {
        await using var stack = await Stack.StartAsync();
        stack.Write("greet.json", Flows.Greet);
        var processId = await stack.DeployAsync("greet.json");
        var jobId = await stack.SubmitAsync(processId, new { who = "Confidential Customer" });
        var viewerToken = await Stack.PostAsync(stack.Admin, "/api/orchestrator/tokens", new { name = "dashboard", role = "Viewer" }, 201);
        var operatorToken = await Stack.PostAsync(stack.Admin, "/api/orchestrator/tokens", new { name = "ops", role = "Operator" }, 201);
        using var viewer = stack.Client(viewerToken.GetProperty("token").GetString());
        using var operatorClient = stack.Client(operatorToken.GetProperty("token").GetString());

        await Stack.PostAsync(viewer, "/api/orchestrator/jobs", new { processId }, 403);
        await Stack.PostAsync(operatorClient, "/api/orchestrator/publish", new { project = "project", path = "greet.json" }, 403);
        await Stack.PostAsync(operatorClient, "/api/orchestrator/runners", new { name = "x" }, 403);
        await Stack.PostAsync(operatorClient, "/api/orchestrator/jobs", new { processId }, 201);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/orchestrator/audit", Ct)).StatusCode);

        var asViewer = await Stack.GetAsync(viewer, $"/api/orchestrator/jobs/{jobId}");
        Assert.True(asViewer.GetProperty("dataHidden").GetBoolean());
        Assert.False(asViewer.TryGetProperty("arguments", out _));
        Assert.DoesNotContain("Confidential", asViewer.ToString(), StringComparison.Ordinal);
        Assert.Contains("Confidential", (await Stack.GetAsync(operatorClient, $"/api/orchestrator/jobs/{jobId}")).ToString(), StringComparison.Ordinal);

        var tokens = await Stack.GetAsync(stack.Admin, "/api/orchestrator/tokens");
        Assert.DoesNotContain("hash", tokens.ToString(), StringComparison.OrdinalIgnoreCase);
        await Stack.PostAsync(stack.Admin, $"/api/orchestrator/tokens/{viewerToken.GetProperty("tokenId").GetString()}/revoke", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.GetAsync("/api/orchestrator/jobs", Ct)).StatusCode);

        // A browser without the start-link session gets nothing.
        using var anonymous = stack.Client(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/orchestrator/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task RunnerCheckCommand_ReportsTheConnection_AndARefusedToken()
    {
        await using var stack = await Stack.StartAsync();
        var (_, token) = await stack.CreateRunnerAsync("checked");
        var folder = Path.Combine(stack.Folder, "runner-config");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "runner.token"), token, Ct);
        var config = Path.Combine(folder, "runner.json");
        await File.WriteAllTextAsync(config, $$"""{ "serverUrl": "{{stack.Server.RunnerUri}}", "tokenFile": "runner.token" }""", Ct);

        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await RunnerApplication.RunAsync(["check", "--config", config], output, error, Ct));
        Assert.Contains("token accepted", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(token, output.ToString() + error, StringComparison.Ordinal);

        await File.WriteAllTextAsync(Path.Combine(folder, "runner.token"), token[..^3] + "abc", Ct);
        Assert.Equal(3, await RunnerApplication.RunAsync(["check", "--config", config], output, error, Ct));
        Assert.Equal(2, await RunnerApplication.RunAsync(["run"], output, error, Ct));
    }

    [Fact]
    public async Task RunnerConfiguration_RefusesPlainHttpToARemoteServer_AndForeignTokens()
    {
        var folder = Path.Combine(Path.GetTempPath(), "myrpa-cfg-" + Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "runner.json");
            await File.WriteAllTextAsync(path, """{ "serverUrl": "http://10.0.0.5:5311/" }""", Ct);
            Assert.Null(RunnerConfiguration.Load(path, _ => "myrpa_runner_rnr_x.secret", out var http));
            Assert.Contains("https", http, StringComparison.Ordinal);

            await File.WriteAllTextAsync(path, """{ "serverUrl": "https://10.0.0.5:5311/" }""", Ct);
            Assert.Null(RunnerConfiguration.Load(path, _ => "myrpa_token_tok_x.secret", out var foreign));
            Assert.Contains("not a Runner token", foreign, StringComparison.Ordinal);
            var loaded = RunnerConfiguration.Load(path, _ => "myrpa_runner_rnr_x.secret", out _);
            Assert.DoesNotContain("secret", loaded!.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Stack.TryDelete(folder);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(100, Ct);
        }
    }
}
