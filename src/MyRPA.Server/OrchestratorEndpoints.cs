using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyRPA.Contracts.Orchestration;
using MyRPA.Orchestration;
using MyRPA.Plugins;

namespace MyRPA.Server;

/// <summary>
/// The management API (ADR-0049, ADR-0051): <c>/api/orchestrator/*</c> for publishing, deployments, jobs, schedules,
/// Runners, access tokens, policy, audit and diagnostics. Every endpoint has an authenticated actor (the middleware sets
/// it); the orchestrator enforces the role of every change, and these endpoints enforce it for administrative reads.
/// Responses never contain token hashes; job arguments and outputs are shown to operators and above only.
/// </summary>
internal static class OrchestratorEndpoints
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static int StatusOf(OrchestrationFailure failure) => failure switch
    {
        OrchestrationFailure.Invalid => StatusCodes.Status400BadRequest,
        OrchestrationFailure.NotFound => StatusCodes.Status404NotFound,
        OrchestrationFailure.Conflict => StatusCodes.Status409Conflict,
        OrchestrationFailure.Forbidden => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status400BadRequest,
    };

    public static void Map(RouteGroupBuilder api)
    {
        var o = api.MapGroup("/orchestrator");
        o.AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.RequestServices.GetService<Orchestrator>() is null)
            {
                return Error(StatusCodes.Status404NotFound, "The orchestrator is not enabled on this Server (start it with --data).");
            }

            try
            {
                return await next(context).ConfigureAwait(false);
            }
            catch (OrchestrationException ex)
            {
                return Error(StatusOf(ex.Failure), ex.Message);
            }
        });

        o.MapGet("/me", (HttpContext context) =>
        {
            var actor = RequestIdentity.Actor(context);
            return Json(new { name = actor.Name, role = actor.Role, roles = Enum.GetNames<Role>() });
        });

        MapOverview(o);
        MapJobs(o);
        MapProcesses(o);
        MapSchedules(o);
        MapRunners(o);
        MapAdministration(o);
    }

    private static void MapOverview(RouteGroupBuilder o) =>
        o.MapGet("/overview", async (string? range, Orchestrator orchestrator, TimeProvider time, HttpContext context) =>
        {
            var ct = context.RequestAborted;
            var (span, bucket) = range switch
            {
                "7d" => (TimeSpan.FromDays(7), TimeSpan.FromDays(1)),
                "30d" => (TimeSpan.FromDays(30), TimeSpan.FromDays(1)),
                _ => (TimeSpan.FromHours(24), TimeSpan.FromHours(1)),
            };
            var now = time.GetUtcNow();
            var since = Floor(now - span, bucket);
            var stats = await orchestrator.Store.GetStatsAsync(since, bucket, ct).ConfigureAwait(false);
            var buckets = new List<object>();
            for (var start = since; start <= now; start += bucket)
            {
                var b = stats.Buckets.FirstOrDefault(x => x.Start == start);
                buckets.Add(new { start, succeeded = b?.Succeeded ?? 0, failed = b?.Failed ?? 0, cancelled = b?.Cancelled ?? 0, interrupted = b?.Interrupted ?? 0 });
            }

            var runners = await orchestrator.ListRunnersAsync(ct).ConfigureAwait(false);
            var names = runners.ToDictionary(r => r.Runner.RunnerId, r => r.Runner.Name, StringComparer.Ordinal);
            var recent = await orchestrator.Store.QueryJobsAsync(new JobQuery { Limit = 10 }, ct).ConfigureAwait(false);
            var failures = await orchestrator.Store.QueryJobsAsync(new JobQuery { States = [JobState.Failed, JobState.TimedOut, JobState.Interrupted], Limit = 5 }, ct).ConfigureAwait(false);
            return Json(new
            {
                range = range is "7d" or "30d" ? range : "24h",
                bucketMinutes = (int)bucket.TotalMinutes,
                total = stats.ByState.Values.Sum(),
                byState = stats.ByState,
                buckets,
                runners = new
                {
                    total = runners.Count,
                    byStatus = Enum.GetValues<RunnerStatus>().ToDictionary(s => s.ToString(), s => runners.Count(r => r.Status == s)),
                    activeJobs = runners.Sum(r => r.ActiveJobs),
                    capacity = runners.Where(r => r.Status is RunnerStatus.Available or RunnerStatus.Busy).Sum(r => r.Runner.Capacity),
                },
                recent = recent.Jobs.Select(j => JobSummary(j, names)),
                failures = failures.Jobs.Select(j => JobSummary(j, names)),
            });
        });

    private static void MapJobs(RouteGroupBuilder o)
    {
        o.MapGet("/jobs", async (string? state, string? processId, string? runnerId, string? scheduleId, int? offset, int? limit, Orchestrator orchestrator, HttpContext context) =>
        {
            var states = new List<JobState>();
            foreach (var name in (state ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse<JobState>(name, ignoreCase: true, out var parsed))
                {
                    return Error(StatusCodes.Status400BadRequest, $"Unknown job state '{name}'.");
                }

                states.Add(parsed);
            }

            var page = await orchestrator.Store.QueryJobsAsync(
                new JobQuery { States = states, ProcessId = processId, RunnerId = runnerId, ScheduleId = scheduleId, Offset = offset ?? 0, Limit = limit ?? 50 },
                context.RequestAborted).ConfigureAwait(false);
            var names = await RunnerNamesAsync(orchestrator, context.RequestAborted).ConfigureAwait(false);
            return Json(new { total = page.Total, offset = offset ?? 0, jobs = page.Jobs.Select(j => JobSummary(j, names)) });
        });

        o.MapGet("/jobs/{jobId}", async (string jobId, Orchestrator orchestrator, HttpContext context) =>
        {
            var job = await orchestrator.Store.GetJobAsync(jobId, context.RequestAborted).ConfigureAwait(false);
            if (job is null)
            {
                return Error(StatusCodes.Status404NotFound, $"Unknown job '{jobId}'.");
            }

            var actor = RequestIdentity.Actor(context);
            var names = await RunnerNamesAsync(orchestrator, context.RequestAborted).ConfigureAwait(false);
            var waiting = job.State == JobState.Queued ? await orchestrator.ExplainWaitAsync(job, context.RequestAborted).ConfigureAwait(false) : null;
            return Json(new
            {
                job = JobSummary(job, names),
                packageSha256 = job.PackageSha256,
                entryPath = job.EntryPath,
                priority = job.Priority,
                targetRunnerId = job.TargetRunnerId,
                targetGroup = job.TargetGroup,
                timeoutMs = job.TimeoutMs,
                correlationId = job.CorrelationId,
                retryOfJobId = job.RetryOfJobId,
                scheduledFor = job.ScheduledFor,
                executionId = job.ExecutionId,
                eventCount = job.EventCount,
                eventsDropped = job.EventsDropped,
                cancelRequestedBy = job.CancelRequestedBy,
                requiredActivities = job.RequiredActivities,
                requiredPlugins = job.RequiredPlugins,
                attempts = job.Attempts.Select(a => new { a.Number, runnerId = a.RunnerId, runnerName = names.GetValueOrDefault(a.RunnerId), a.AssignedAt, a.StartedAt, a.EndedAt, a.Outcome, a.Reason }),
                // Business data: visible to operators and above only.
                arguments = actor.Has(Role.Operator) ? RawJson(job.ArgumentsJson) : (JsonElement?)null,
                outputs = actor.Has(Role.Operator) && job.OutputsJson is not null ? RawJson(job.OutputsJson) : (JsonElement?)null,
                dataHidden = !actor.Has(Role.Operator),
                waiting,
            });
        });

        o.MapGet("/jobs/{jobId}/events", async (string jobId, int? afterAttempt, long? afterSequence, int? limit, Orchestrator orchestrator, HttpContext context) =>
        {
            var events = await orchestrator.Store.ReadEventsAsync(jobId, afterAttempt ?? 0, afterSequence ?? 0, limit ?? 500, context.RequestAborted).ConfigureAwait(false);
            return Json(new { events = events.Select(e => new { attempt = e.Attempt, sequence = e.Sequence, @event = RawJson(e.Json) }) });
        });

        o.MapPost("/jobs", async (SubmitJobBody body, Orchestrator orchestrator, HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(body.ProcessId))
            {
                return Error(StatusCodes.Status400BadRequest, "'processId' is required.");
            }

            var (job, created) = await orchestrator.SubmitAsync(RequestIdentity.Actor(context), new SubmitJobRequest
            {
                ProcessId = body.ProcessId,
                Version = body.Version,
                EntryPath = Blank(body.EntryPath),
                Arguments = body.Arguments,
                ArgumentText = body.ArgumentText?.Where(a => a.Value is not null).ToDictionary(a => a.Key, a => a.Value!, StringComparer.Ordinal),
                Priority = body.Priority,
                TargetRunnerId = Blank(body.TargetRunnerId),
                TargetGroup = Blank(body.TargetGroup),
                TimeoutMs = body.TimeoutMs,
                IdempotencyKey = Blank(body.IdempotencyKey) ?? Blank(context.Request.Headers["Idempotency-Key"].ToString()),
            }, context.RequestAborted).ConfigureAwait(false);
            return Json(new { job = JobSummary(job, new Dictionary<string, string>()), created }, created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        });

        o.MapPost("/jobs/{jobId}/cancel", async (string jobId, Orchestrator orchestrator, HttpContext context) =>
        {
            var job = await orchestrator.CancelAsync(RequestIdentity.Actor(context), jobId, context.RequestAborted).ConfigureAwait(false);
            return Json(new { job = JobSummary(job, new Dictionary<string, string>()) }, StatusCodes.Status202Accepted);
        });

        o.MapPost("/jobs/{jobId}/retry", async (string jobId, RetryBody? body, Orchestrator orchestrator, HttpContext context) =>
        {
            var (job, created) = await orchestrator.RetryAsync(RequestIdentity.Actor(context), jobId, Blank(body?.IdempotencyKey), context.RequestAborted).ConfigureAwait(false);
            return Json(new { job = JobSummary(job, new Dictionary<string, string>()), created }, created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        });
    }

    private static void MapProcesses(RouteGroupBuilder o)
    {
        o.MapGet("/processes", async (Orchestrator orchestrator, HttpContext context) =>
        {
            var ct = context.RequestAborted;
            var processes = await orchestrator.Store.ListProcessesAsync(ct).ConfigureAwait(false);
            var schedules = await orchestrator.Store.ListSchedulesAsync(ct).ConfigureAwait(false);
            var list = new List<object>();
            foreach (var process in processes.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                var lastJobs = (await orchestrator.Store.QueryJobsAsync(new JobQuery { ProcessId = process.ProcessId, Limit = 1 }, ct).ConfigureAwait(false)).Jobs;
                var last = lastJobs.Count > 0 ? lastJobs[0] : null;
                var latest = await orchestrator.Store.GetPackageAsync(process.ProcessId, process.LatestVersion, ct).ConfigureAwait(false);
                list.Add(new
                {
                    process = ProcessView(process),
                    latest = latest is null ? null : VersionSummary(latest),
                    schedules = schedules.Count(s => s.ProcessId == process.ProcessId),
                    enabledSchedules = schedules.Count(s => s.ProcessId == process.ProcessId && s.Enabled),
                    lastJob = last is null ? null : new { last.JobId, last.State, last.CreatedAt, last.CompletedAt, last.Version },
                });
            }

            return Json(new { processes = list });
        });

        o.MapGet("/processes/{processId}", async (string processId, Orchestrator orchestrator, HttpContext context) =>
        {
            var ct = context.RequestAborted;
            var process = await orchestrator.Store.GetProcessAsync(processId, ct).ConfigureAwait(false);
            if (process is null)
            {
                return Error(StatusCodes.Status404NotFound, $"Unknown process '{processId}'.");
            }

            var versions = await orchestrator.Store.ListPackagesAsync(processId, ct).ConfigureAwait(false);
            var schedules = (await orchestrator.Store.ListSchedulesAsync(ct).ConfigureAwait(false)).Where(s => s.ProcessId == processId);
            var names = await RunnerNamesAsync(orchestrator, ct).ConfigureAwait(false);
            var jobs = await orchestrator.Store.QueryJobsAsync(new JobQuery { ProcessId = processId, Limit = 20 }, ct).ConfigureAwait(false);
            var runners = await orchestrator.ListRunnersAsync(ct).ConfigureAwait(false);
            var deployed = process.DeployedVersion is { } v ? versions.FirstOrDefault(p => p.Version == v) : null;
            return Json(new
            {
                process = ProcessView(process),
                versions = versions.Select(VersionDetail),
                schedules = schedules.Select(ScheduleView),
                jobs = new { total = jobs.Total, items = jobs.Jobs.Select(j => JobSummary(j, names)) },
                eligibleRunners = deployed is null ? null : runners.Select(r => new
                {
                    runnerId = r.Runner.RunnerId,
                    name = r.Runner.Name,
                    status = r.Status,
                    reason = Eligibility(process, deployed, r.Runner),
                }),
            });
        });

        o.MapPost("/publish", async (PublishBody body, Orchestrator orchestrator, ProjectStore projects, IServiceProvider services, HttpContext context) =>
        {
            var ct = context.RequestAborted;
            // A package is a project with entry points; 'path' alone publishes a project with that single entry point.
            IReadOnlyList<string> entryPoints = body.EntryPoints is { Count: > 0 } listed ? [.. listed.Select(e => e ?? string.Empty)] : body.Path is null ? [] : [body.Path];
            if (body.Project is null || entryPoints.Count == 0 || projects.Find(body.Project) is not { } root)
            {
                return Error(StatusCodes.Status400BadRequest, "'project' must name a registered project and 'entryPoints' (or 'path') its workflows to start.");
            }

            foreach (var entryPoint in entryPoints)
            {
                if (!projects.TryResolve(body.Project, entryPoint, out _, out var pathError))
                {
                    return Error(StatusCodes.Status400BadRequest, pathError);
                }
            }

            // The saved project files (unsaved editor changes are not published).
            var files = new List<KeyValuePair<string, string>>();
            foreach (var file in ProjectStore.List(root))
            {
                if (projects.TryResolve(body.Project, file.Path, out var full, out _) && await projects.ReadAsync(full, ct).ConfigureAwait(false) is { } read)
                {
                    files.Add(new(file.Path, Encoding.UTF8.GetString(read.Content)));
                }
            }

            var registry = services.GetService<IPluginRegistry>();
            var owners = new Dictionary<string, PackagePlugin>(StringComparer.Ordinal);
            foreach (var plugin in registry?.Plugins ?? [])
            {
                foreach (var activity in plugin.Manifest.Activities)
                {
                    owners.TryAdd(activity.Value, new PackagePlugin(plugin.Manifest.Id.Value, plugin.Manifest.Version.ToString()));
                }
            }

            var (process, package) = await orchestrator.PublishAsync(RequestIdentity.Actor(context), root.Name, entryPoints, files, body.Notes, owners.GetValueOrDefault, ct).ConfigureAwait(false);
            return Json(new { process = ProcessView(process), version = VersionDetail(package) }, StatusCodes.Status201Created);
        });

        o.MapGet("/processes/{processId}/versions/{version:int}", async (string processId, int version, Orchestrator orchestrator, HttpContext context) =>
        {
            var package = await orchestrator.Store.GetPackageAsync(processId, version, context.RequestAborted).ConfigureAwait(false);
            return package is null ? Error(StatusCodes.Status404NotFound, "Unknown version.") : Json(VersionDetail(package));
        });

        o.MapPost("/processes/{processId}/deploy", async (string processId, DeployBody body, Orchestrator orchestrator, HttpContext context) =>
            Json(ProcessView(await orchestrator.DeployAsync(RequestIdentity.Actor(context), processId, body.Version, context.RequestAborted).ConfigureAwait(false))));

        o.MapPost("/processes/{processId}/undeploy", async (string processId, Orchestrator orchestrator, HttpContext context) =>
            Json(ProcessView(await orchestrator.UndeployAsync(RequestIdentity.Actor(context), processId, context.RequestAborted).ConfigureAwait(false))));

        o.MapPost("/processes/{processId}/enabled", async (string processId, EnabledBody body, Orchestrator orchestrator, HttpContext context) =>
            Json(ProcessView(await orchestrator.SetEnabledAsync(RequestIdentity.Actor(context), processId, body.Enabled, context.RequestAborted).ConfigureAwait(false))));

        o.MapPost("/processes/{processId}/settings", async (string processId, ProcessSettingsBody body, Orchestrator orchestrator, HttpContext context) =>
            Json(ProcessView(await orchestrator.ConfigureAsync(RequestIdentity.Actor(context), processId, Blank(body.TargetGroup), Blank(body.TargetRunnerId), body.Priority, body.TimeoutMs, context.RequestAborted).ConfigureAwait(false))));
    }

    private static void MapSchedules(RouteGroupBuilder o)
    {
        o.MapGet("/schedules", async (Orchestrator orchestrator, HttpContext context) =>
        {
            var processes = (await orchestrator.Store.ListProcessesAsync(context.RequestAborted).ConfigureAwait(false)).ToDictionary(p => p.ProcessId, p => p.Name, StringComparer.Ordinal);
            var schedules = await orchestrator.Store.ListSchedulesAsync(context.RequestAborted).ConfigureAwait(false);
            return Json(new { schedules = schedules.Select(s => new { schedule = ScheduleView(s), processName = processes.GetValueOrDefault(s.ProcessId) }) });
        });

        o.MapGet("/schedules/preview", (string? kind, string? cron, int? intervalMinutes, DateTimeOffset? startAt, string? timeZone, TimeProvider time) =>
        {
            if (!TryKind(kind, out var parsedKind))
            {
                return Json(new { valid = false, error = "The kind must be Once, Interval or Cron." });
            }

            var now = time.GetUtcNow();
            var schedule = new ScheduleRecord
            {
                ScheduleId = "preview",
                ProcessId = "preview",
                EntryPath = "preview",
                Name = "preview",
                Kind = parsedKind,
                Cron = cron,
                IntervalMinutes = intervalMinutes,
                StartAt = startAt,
                TimeZone = timeZone ?? string.Empty,
                CreatedAt = now,
                CreatedBy = "preview",
            };
            if (ScheduleTimes.Validate(schedule) is { } error)
            {
                return Json(new { valid = false, error });
            }

            var next = new List<DateTimeOffset>();
            var after = now;
            while (next.Count < 5 && ScheduleTimes.Next(schedule, after) is { } fire)
            {
                next.Add(fire);
                after = fire;
            }

            return Json(new { valid = true, next });
        });

        o.MapGet("/timezones", () =>
        {
            var zones = TimeZoneInfo.GetSystemTimeZones()
                .Select(z => new { id = TimeZoneInfo.TryConvertWindowsIdToIanaId(z.Id, out var iana) ? iana : z.Id, display = z.DisplayName })
                .DistinctBy(z => z.id)
                .OrderBy(z => z.id, StringComparer.Ordinal);
            var local = TimeZoneInfo.Local.Id;
            return Json(new { local = TimeZoneInfo.TryConvertWindowsIdToIanaId(local, out var localIana) ? localIana : local, zones });
        });

        o.MapPost("/processes/{processId}/schedules", async (string processId, ScheduleBody body, Orchestrator orchestrator, HttpContext context) =>
        {
            var schedule = await orchestrator.CreateScheduleAsync(RequestIdentity.Actor(context), processId, Definition(body), context.RequestAborted).ConfigureAwait(false);
            return Json(ScheduleView(schedule), StatusCodes.Status201Created);
        });

        o.MapPut("/schedules/{scheduleId}", async (string scheduleId, ScheduleBody body, Orchestrator orchestrator, HttpContext context) =>
            Json(ScheduleView(await orchestrator.UpdateScheduleAsync(RequestIdentity.Actor(context), scheduleId, Definition(body), context.RequestAborted).ConfigureAwait(false))));

        o.MapPost("/schedules/{scheduleId}/enabled", async (string scheduleId, EnabledBody body, Orchestrator orchestrator, HttpContext context) =>
            Json(ScheduleView(await orchestrator.SetScheduleEnabledAsync(RequestIdentity.Actor(context), scheduleId, body.Enabled, context.RequestAborted).ConfigureAwait(false))));

        o.MapDelete("/schedules/{scheduleId}", async (string scheduleId, Orchestrator orchestrator, HttpContext context) =>
        {
            await orchestrator.DeleteScheduleAsync(RequestIdentity.Actor(context), scheduleId, context.RequestAborted).ConfigureAwait(false);
            return Results.NoContent();
        });
    }

    private static void MapRunners(RouteGroupBuilder o)
    {
        o.MapGet("/runners", async (Orchestrator orchestrator, HttpContext context) =>
            Json(new { runners = (await orchestrator.ListRunnersAsync(context.RequestAborted).ConfigureAwait(false)).Select(RunnerView) }));

        o.MapGet("/runners/{runnerId}", async (string runnerId, Orchestrator orchestrator, HttpContext context) =>
        {
            var runner = (await orchestrator.ListRunnersAsync(context.RequestAborted).ConfigureAwait(false)).FirstOrDefault(r => r.Runner.RunnerId == runnerId);
            if (runner is null)
            {
                return Error(StatusCodes.Status404NotFound, $"Unknown Runner '{runnerId}'.");
            }

            var jobs = await orchestrator.Store.QueryJobsAsync(new JobQuery { RunnerId = runnerId, Limit = 20 }, context.RequestAborted).ConfigureAwait(false);
            var names = new Dictionary<string, string> { [runner.Runner.RunnerId] = runner.Runner.Name };
            return Json(new
            {
                runner = RunnerView(runner),
                plugins = runner.Runner.Plugins,
                activities = runner.Runner.Activities,
                pluginErrors = runner.Runner.PluginErrors,
                jobs = jobs.Jobs.Select(j => JobSummary(j, names)),
            });
        });

        o.MapPost("/runners", async (CreateRunnerBody body, Orchestrator orchestrator, ServerListeners listeners, HttpContext context) =>
        {
            var (runner, token) = await orchestrator.CreateRunnerAsync(RequestIdentity.Actor(context), body.Name ?? string.Empty, body.Group, body.MaxConcurrency ?? 1, context.RequestAborted).ConfigureAwait(false);
            return Json(new { runnerId = runner.RunnerId, name = runner.Name, token, serverUrl = ServerUrlFor(listeners, context) }, StatusCodes.Status201Created);
        });

        o.MapMethods("/runners/{runnerId}", ["PATCH"], async (string runnerId, UpdateRunnerBody body, Orchestrator orchestrator, HttpContext context) =>
        {
            var runner = await orchestrator.UpdateRunnerAsync(RequestIdentity.Actor(context), runnerId, body.Group, body.MaxConcurrency, body.Paused, context.RequestAborted).ConfigureAwait(false);
            return Json(new { runnerId = runner.RunnerId, name = runner.Name, group = runner.Group, maxConcurrency = runner.MaxConcurrency, paused = runner.Paused });
        });

        o.MapPost("/runners/{runnerId}/revoke", async (string runnerId, Orchestrator orchestrator, HttpContext context) =>
        {
            var runner = await orchestrator.RevokeRunnerAsync(RequestIdentity.Actor(context), runnerId, context.RequestAborted).ConfigureAwait(false);
            return Json(new { runnerId = runner.RunnerId, disabled = runner.Disabled });
        });

        o.MapPost("/runners/{runnerId}/token", async (string runnerId, Orchestrator orchestrator, ServerListeners listeners, HttpContext context) =>
        {
            var token = await orchestrator.RotateRunnerTokenAsync(RequestIdentity.Actor(context), runnerId, context.RequestAborted).ConfigureAwait(false);
            return Json(new { runnerId, token, serverUrl = ServerUrlFor(listeners, context) });
        });
    }

    private static void MapAdministration(RouteGroupBuilder o)
    {
        o.MapGet("/tokens", async (Orchestrator orchestrator, HttpContext context) =>
        {
            Orchestrator.Require(RequestIdentity.Actor(context), Role.Admin);
            var tokens = await orchestrator.Store.ListAccessTokensAsync(context.RequestAborted).ConfigureAwait(false);
            return Json(new { tokens = tokens.Select(t => new { t.TokenId, t.Name, t.Role, t.CreatedAt, t.CreatedBy, t.RevokedAt }) });
        });

        o.MapPost("/tokens", async (CreateTokenBody body, Orchestrator orchestrator, HttpContext context) =>
        {
            if (!Enum.TryParse<Role>(body.Role, ignoreCase: true, out var role) || !Enum.IsDefined(role))
            {
                return Error(StatusCodes.Status400BadRequest, "The role must be Viewer, Operator, Developer or Admin.");
            }

            var (record, token) = await orchestrator.CreateAccessTokenAsync(RequestIdentity.Actor(context), body.Name ?? string.Empty, role, context.RequestAborted).ConfigureAwait(false);
            return Json(new { record.TokenId, record.Name, record.Role, token }, StatusCodes.Status201Created);
        });

        o.MapPost("/tokens/{tokenId}/revoke", async (string tokenId, Orchestrator orchestrator, HttpContext context) =>
        {
            var record = await orchestrator.RevokeAccessTokenAsync(RequestIdentity.Actor(context), tokenId, context.RequestAborted).ConfigureAwait(false);
            return Json(new { record.TokenId, record.RevokedAt });
        });

        o.MapGet("/policy", async (Orchestrator orchestrator, HttpContext context) =>
            Json(await orchestrator.Store.GetPolicyAsync(context.RequestAborted).ConfigureAwait(false)));

        o.MapPut("/policy", async (OrchestrationPolicy body, Orchestrator orchestrator, HttpContext context) =>
            Json(await orchestrator.SetPolicyAsync(RequestIdentity.Actor(context), body, context.RequestAborted).ConfigureAwait(false)));

        o.MapGet("/audit", async (int? offset, int? limit, Orchestrator orchestrator, HttpContext context) =>
            Json(new { entries = await orchestrator.ListAuditAsync(RequestIdentity.Actor(context), offset ?? 0, limit ?? 100, context.RequestAborted).ConfigureAwait(false) }));

        o.MapGet("/diagnostics", async (Orchestrator orchestrator, ServerListeners listeners, IServiceProvider services, TimeProvider time, HttpContext context) =>
        {
            Orchestrator.Require(RequestIdentity.Actor(context), Role.Admin);
            var ct = context.RequestAborted;
            var store = await orchestrator.Store.GetDiagnosticsAsync(ct).ConfigureAwait(false);
            var registry = services.GetService<IPluginRegistry>();
            var runners = await orchestrator.ListRunnersAsync(ct).ConfigureAwait(false);
            var processes = await orchestrator.Store.ListProcessesAsync(ct).ConfigureAwait(false);
            var compatibility = new List<object>();
            foreach (var process in processes.Where(p => p.DeployedVersion is not null))
            {
                var package = await orchestrator.Store.GetPackageAsync(process.ProcessId, process.DeployedVersion!.Value, ct).ConfigureAwait(false);
                if (package is null)
                {
                    continue;
                }

                compatibility.Add(new
                {
                    processId = process.ProcessId,
                    name = process.Name,
                    version = package.Version,
                    plugins = package.Manifest.Plugins,
                    runners = runners.Select(r => new { runnerId = r.Runner.RunnerId, name = r.Runner.Name, reason = Eligibility(process, package, r.Runner) }),
                });
            }

            return Json(new
            {
                server = new
                {
                    version = typeof(ServerApplication).Assembly.GetName().Version?.ToString(),
                    startedAt = listeners.StartedAt,
                    now = time.GetUtcNow(),
                    runnerListener = listeners.RunnerUri?.ToString(),
                    runnerProtocol = RunnerProtocol.Version,
                    heartbeatSeconds = orchestrator.Options.HeartbeatInterval.TotalSeconds,
                    leaseSeconds = orchestrator.Options.LeaseDuration.TotalSeconds,
                },
                store,
                plugins = (registry?.Plugins ?? []).Select(p => new { id = p.Manifest.Id.Value, version = p.Manifest.Version.ToString(), activities = p.Manifest.Activities.Count }),
                pluginDiagnostics = (registry?.Diagnostics ?? []).Select(d => new { code = d.Code, severity = d.Severity.ToString(), message = d.Message, pluginId = d.PluginId }),
                compatibility,
            });
        });
    }

    private static object JobSummary(JobRecord job, IReadOnlyDictionary<string, string> runnerNames) => new
    {
        jobId = job.JobId,
        processId = job.ProcessId,
        processName = job.ProcessName,
        entryPath = job.EntryPath,
        version = job.Version,
        state = job.State,
        createdAt = job.CreatedAt,
        assignedAt = job.AssignedAt,
        startedAt = job.StartedAt,
        completedAt = job.CompletedAt,
        durationMs = job.DurationMs,
        runnerId = job.RunnerId,
        runnerName = job.RunnerId is null ? null : runnerNames.GetValueOrDefault(job.RunnerId),
        source = job.Source,
        scheduleId = job.ScheduleId,
        submittedBy = job.SubmittedBy,
        attempts = job.AttemptCount,
        cancelRequested = job.CancelRequested,
        error = job.Error,
    };

    private static object ProcessView(ProcessRecord p) => new
    {
        processId = p.ProcessId,
        name = p.Name,
        project = p.Project,
        createdAt = p.CreatedAt,
        latestVersion = p.LatestVersion,
        deployedVersion = p.DeployedVersion,
        deployedAt = p.DeployedAt,
        deployedBy = p.DeployedBy,
        enabled = p.Enabled,
        targetGroup = p.TargetGroup,
        targetRunnerId = p.TargetRunnerId,
        priority = p.Priority,
        timeoutMs = p.TimeoutMs,
        // Published: versions exist, none deployed. Deployed: jobs and schedules use a version. Disabled: nothing is accepted.
        status = !p.Enabled ? "Disabled" : p.DeployedVersion is null ? "Published" : "Deployed",
    };

    private static object VersionSummary(PackageRecord p) => new
    {
        version = p.Version,
        sha256 = p.Sha256,
        publishedAt = p.Manifest.PublishedAt,
        publishedBy = p.Manifest.PublishedBy,
    };

    private static object VersionDetail(PackageRecord p) => new
    {
        version = p.Version,
        sha256 = p.Sha256,
        size = p.Size,
        format = p.Manifest.Format,
        project = p.Manifest.Project,
        entryPoints = p.Manifest.EntryPoints,
        publishedAt = p.Manifest.PublishedAt,
        publishedBy = p.Manifest.PublishedBy,
        notes = p.Manifest.Notes,
        files = p.Manifest.Files,
        activities = p.Manifest.Activities,
        plugins = p.Manifest.Plugins,
        excluded = p.Manifest.Excluded,
    };

    private static object ScheduleView(ScheduleRecord s) => new
    {
        scheduleId = s.ScheduleId,
        processId = s.ProcessId,
        entryPath = s.EntryPath,
        name = s.Name,
        kind = s.Kind,
        cron = s.Cron,
        intervalMinutes = s.IntervalMinutes,
        startAt = s.StartAt,
        timeZone = s.TimeZone,
        enabled = s.Enabled,
        misfire = s.Misfire,
        overlap = s.Overlap,
        arguments = RawJson(s.ArgumentsJson),
        createdAt = s.CreatedAt,
        createdBy = s.CreatedBy,
        lastFireAt = s.LastFireAt,
        nextFireAt = s.NextFireAt,
        lastOutcome = s.LastOutcome,
    };

    private static object RunnerView(RunnerView view) => new
    {
        runnerId = view.Runner.RunnerId,
        name = view.Runner.Name,
        group = view.Runner.Group,
        status = view.Status,
        activeJobs = view.ActiveJobs,
        capacity = view.Runner.Capacity,
        maxConcurrency = view.Runner.MaxConcurrency,
        paused = view.Runner.Paused,
        disabled = view.Runner.Disabled,
        createdAt = view.Runner.CreatedAt,
        registeredAt = view.Runner.RegisteredAt,
        lastSeenAt = view.Runner.LastSeenAt,
        version = view.Runner.Version,
        operatingSystem = view.Runner.OperatingSystem,
        architecture = view.Runner.Architecture,
        machineName = view.Runner.MachineName,
        service = view.Runner.Service,
        pluginCount = view.Runner.Plugins.Count,
        activityCount = view.Runner.Activities.Count,
        pluginErrors = view.Runner.PluginErrors.Count,
    };

    private static string Eligibility(ProcessRecord process, PackageRecord package, RunnerRecord runner) =>
        Orchestrator.Ineligibility(
            new JobRecord
            {
                JobId = "check",
                ProcessId = process.ProcessId,
                ProcessName = process.Name,
                Version = package.Version,
                PackageSha256 = package.Sha256,
                EntryPath = package.Manifest.EntryPoints[0].Path,
                RequiredActivities = package.Manifest.Activities,
                RequiredPlugins = package.Manifest.Plugins,
                TargetGroup = process.TargetGroup,
                TargetRunnerId = process.TargetRunnerId,
                CreatedAt = DateTimeOffset.MinValue,
                CorrelationId = "check",
                SubmittedBy = "check",
            },
            runner) ?? "eligible";

    private static async Task<Dictionary<string, string>> RunnerNamesAsync(Orchestrator orchestrator, CancellationToken cancellationToken) =>
        (await orchestrator.Store.ListRunnersAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.RunnerId, r => r.Name, StringComparer.Ordinal);

    private static ScheduleDefinition Definition(ScheduleBody body)
    {
        if (!TryKind(body.Kind, out var kind))
        {
            throw new OrchestrationException(OrchestrationFailure.Invalid, "The kind must be Once, Interval or Cron.");
        }

        return new ScheduleDefinition
        {
            Name = body.Name ?? string.Empty,
            Kind = kind,
            EntryPath = Blank(body.EntryPath),
            Cron = body.Cron,
            IntervalMinutes = body.IntervalMinutes,
            StartAt = body.StartAt,
            TimeZone = body.TimeZone ?? string.Empty,
            Misfire = string.Equals(body.Misfire, "Skip", StringComparison.OrdinalIgnoreCase) ? MisfirePolicy.Skip : MisfirePolicy.RunOnce,
            Overlap = string.Equals(body.Overlap, "Allow", StringComparison.OrdinalIgnoreCase) ? OverlapPolicy.Allow : OverlapPolicy.Skip,
            Arguments = body.Arguments,
            Enabled = body.Enabled ?? true,
        };
    }

    private static bool TryKind(string? text, out ScheduleKind kind) =>
        Enum.TryParse(text, ignoreCase: true, out kind) && Enum.IsDefined(kind);

    private static string ServerUrlFor(ServerListeners listeners, HttpContext context) =>
        listeners.RunnerUri?.ToString() ?? $"{context.Request.Scheme}://{context.Request.Host}/";

    private static DateTimeOffset Floor(DateTimeOffset time, TimeSpan bucket) => new(time.UtcTicks - (time.UtcTicks % bucket.Ticks), TimeSpan.Zero);

    private static JsonElement RawJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult Json(object value, int status = StatusCodes.Status200OK) => Results.Json(value, _json, statusCode: status);

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, _json, statusCode: status);
}

/// <summary><c>POST /api/orchestrator/publish</c>.</summary>
internal sealed record PublishBody(string? Project, string? Path, List<string?>? EntryPoints, string? Notes);

/// <summary><c>POST /api/orchestrator/processes/{id}/deploy</c>.</summary>
internal sealed record DeployBody(int Version);

/// <summary>Enable or disable a deployment or schedule.</summary>
internal sealed record EnabledBody(bool Enabled);

/// <summary><c>POST /api/orchestrator/processes/{id}/settings</c>.</summary>
internal sealed record ProcessSettingsBody(string? TargetGroup, string? TargetRunnerId, int Priority, int? TimeoutMs);

/// <summary><c>POST /api/orchestrator/jobs</c>.</summary>
internal sealed record SubmitJobBody(
    string? ProcessId,
    int? Version,
    string? EntryPath,
    Dictionary<string, JsonElement>? Arguments,
    Dictionary<string, string?>? ArgumentText,
    int? Priority,
    string? TargetRunnerId,
    string? TargetGroup,
    int? TimeoutMs,
    string? IdempotencyKey);

/// <summary><c>POST /api/orchestrator/jobs/{id}/retry</c>.</summary>
internal sealed record RetryBody(string? IdempotencyKey);

/// <summary>A schedule definition.</summary>
internal sealed record ScheduleBody(
    string? Name,
    string? Kind,
    string? EntryPath,
    string? Cron,
    int? IntervalMinutes,
    DateTimeOffset? StartAt,
    string? TimeZone,
    string? Misfire,
    string? Overlap,
    Dictionary<string, JsonElement>? Arguments,
    bool? Enabled);

/// <summary><c>POST /api/orchestrator/runners</c>.</summary>
internal sealed record CreateRunnerBody(string? Name, string? Group, int? MaxConcurrency);

/// <summary><c>PATCH /api/orchestrator/runners/{id}</c>.</summary>
internal sealed record UpdateRunnerBody(string? Group, int? MaxConcurrency, bool? Paused);

/// <summary><c>POST /api/orchestrator/tokens</c>.</summary>
internal sealed record CreateTokenBody(string? Name, string? Role);
