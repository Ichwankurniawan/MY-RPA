using System.Text.Json;
using MyRPA.Contracts.Execution;
using MyRPA.Contracts.Orchestration;

namespace MyRPA.Orchestration;

/// <summary>A Runner with its computed status and workload.</summary>
/// <param name="Runner">The record.</param>
/// <param name="Status">Its status now.</param>
/// <param name="ActiveJobs">Jobs it holds (assigned or running).</param>
public sealed record RunnerView(RunnerRecord Runner, RunnerStatus Status, int ActiveJobs);

/// <summary>Runners: administration, the Runner protocol and lease expiry (ADR-0049).</summary>
public sealed partial class Orchestrator
{
    /// <summary>Most characters of a stored log message.</summary>
    private const int MaxEventMessageLength = 4000;

    /// <summary>Largest stored outputs object.</summary>
    private const int MaxOutputsLength = 64 * 1024;

    /// <summary>Creates a Runner identity and returns its token (shown once; only its hash is stored).</summary>
    public async Task<(RunnerRecord Runner, string Token)> CreateRunnerAsync(Actor actor, string name, string? group, int maxConcurrency, CancellationToken cancellationToken)
    {
        Require(actor, Role.Admin);
        name = (name ?? string.Empty).Trim();
        if (name.Length is 0 or > 80 || maxConcurrency is < 1 or > 64)
        {
            throw new OrchestrationException(OrchestrationFailure.Invalid, "A Runner needs a name (up to 80 characters) and a concurrency of 1 to 64.");
        }

        var id = _ids.NewId("rnr");
        var (token, hash) = TokenSecrets.Create(TokenSecrets.RunnerPrefix, id);
        var runner = new RunnerRecord
        {
            RunnerId = id,
            Name = name,
            Group = string.IsNullOrWhiteSpace(group) ? null : group.Trim(),
            TokenHash = hash,
            CreatedAt = _time.GetUtcNow(),
            MaxConcurrency = maxConcurrency,
        };
        if (!await _store.InsertRunnerAsync(runner, cancellationToken).ConfigureAwait(false))
        {
            throw new OrchestrationException(OrchestrationFailure.Conflict, $"A Runner named '{name}' already exists.");
        }

        await AuditAsync(actor, "runner.create", id, $"{name}, group={runner.Group ?? "none"}, concurrency={maxConcurrency}", cancellationToken).ConfigureAwait(false);
        return (runner, token);
    }

    /// <summary>Changes a Runner's group, concurrency cap or paused state.</summary>
    public Task<RunnerRecord> UpdateRunnerAsync(Actor actor, string runnerId, string? group, int? maxConcurrency, bool? paused, CancellationToken cancellationToken)
    {
        Require(actor, Role.Admin);
        if (maxConcurrency is < 1 or > 64)
        {
            throw new OrchestrationException(OrchestrationFailure.Invalid, "Concurrency must be 1 to 64.");
        }

        return ChangeRunnerAsync(actor, runnerId, runner =>
        {
            var updated = runner with
            {
                Group = group is null ? runner.Group : string.IsNullOrWhiteSpace(group) ? null : group.Trim(),
                MaxConcurrency = maxConcurrency ?? runner.MaxConcurrency,
                Paused = paused ?? runner.Paused,
            };
            return (updated, paused is { } p ? (p ? "runner.pause" : "runner.resume") : "runner.update", $"group={updated.Group ?? "none"}, concurrency={updated.MaxConcurrency}");
        }, cancellationToken);
    }

    /// <summary>Revokes a Runner: its token stops working at once; its running jobs become interrupted when their leases end.</summary>
    public Task<RunnerRecord> RevokeRunnerAsync(Actor actor, string runnerId, CancellationToken cancellationToken)
    {
        Require(actor, Role.Admin);
        return ChangeRunnerAsync(actor, runnerId, runner => (runner with { Disabled = true }, "runner.revoke", runner.Name), cancellationToken);
    }

    /// <summary>Issues a new token for a Runner (the old one stops working).</summary>
    public async Task<string> RotateRunnerTokenAsync(Actor actor, string runnerId, CancellationToken cancellationToken)
    {
        Require(actor, Role.Admin);
        string? token = null;
        await ChangeRunnerAsync(actor, runnerId, runner =>
        {
            var (text, hash) = TokenSecrets.Create(TokenSecrets.RunnerPrefix, runner.RunnerId);
            token = text;
            return (runner with { TokenHash = hash, Disabled = false }, "runner.rotate-token", runner.Name);
        }, cancellationToken).ConfigureAwait(false);
        return token!;
    }

    /// <summary>The Runner a token belongs to, if the token is valid and not revoked.</summary>
    public async Task<RunnerRecord?> AuthenticateRunnerAsync(string? token, CancellationToken cancellationToken)
    {
        if (!TokenSecrets.TryParse(token, TokenSecrets.RunnerPrefix, out var id, out var secret))
        {
            return null;
        }

        var runner = await _store.GetRunnerAsync(id, cancellationToken).ConfigureAwait(false);
        return runner is { Disabled: false } && TokenSecrets.Verify(secret, runner.TokenHash) ? runner : null;
    }

    /// <summary>All Runners with their status and workload.</summary>
    public async Task<IReadOnlyList<RunnerView>> ListRunnersAsync(CancellationToken cancellationToken)
    {
        var runners = await _store.ListRunnersAsync(cancellationToken).ConfigureAwait(false);
        var active = await _store.ListActiveJobsAsync(cancellationToken).ConfigureAwait(false);
        var policy = await _store.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        return [.. runners.Select(r =>
        {
            var held = active.Count(j => j.RunnerId == r.RunnerId && j.State is JobState.Assigned or JobState.Running);
            return new RunnerView(r, StatusOf(r, held, now, policy), held);
        })];
    }

    /// <summary>
    /// A Runner (re)connects. Jobs the Server thinks it holds but it does not report were lost with its previous process:
    /// not started ones are requeued; running ones become interrupted (outcome unknown).
    /// </summary>
    public async Task<RunnerRegisterResponse> RegisterAsync(RunnerRecord runner, RunnerRegisterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var current = await _store.GetRunnerAsync(runner.RunnerId, cancellationToken).ConfigureAwait(false) ?? runner;
            var updated = current with
            {
                RegisteredAt = now,
                LastSeenAt = now,
                Version = Truncate(request.RunnerVersion, 40),
                OperatingSystem = Truncate(request.OperatingSystem, 120),
                Architecture = Truncate(request.Architecture, 20),
                MachineName = request.MachineName is null ? null : Truncate(request.MachineName, 120),
                Service = request.Service,
                ReportedConcurrency = Math.Clamp(request.MaxConcurrency, 1, 64),
                Plugins = [.. request.Plugins.Take(200).Select(p => new PackagePlugin(Truncate(p.Id, 120), Truncate(p.Version, 40)))],
                Activities = [.. request.Activities.Take(2000).Select(a => Truncate(a, 120)).Distinct(StringComparer.Ordinal)],
                PluginErrors = [.. request.PluginErrors.Take(50).Select(e => Truncate(e, 500))],
            };
            await UpdateOrThrowAsync(_store.UpdateRunnerAsync(updated, cancellationToken)).ConfigureAwait(false);

            var held = request.ActiveJobs.Select(j => j.LeaseId).ToHashSet(StringComparer.Ordinal);
            foreach (var job in await _store.ListActiveJobsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (job.RunnerId != runner.RunnerId || job.State is not (JobState.Assigned or JobState.Running) || (job.LeaseId is not null && held.Contains(job.LeaseId)))
                {
                    continue;
                }

                var lost = job.State == JobState.Assigned
                    ? Requeue(job, now, "Released", "The Runner restarted before starting the job.")
                    : Interrupt(job, now, "The Runner restarted while the job ran; the outcome is unknown.");
                await _store.UpdateJobAsync(lost, cancellationToken).ConfigureAwait(false);
            }

            LogRegistered(_logger, runner.RunnerId, updated.Activities.Count);
            Pulse();
            return new RunnerRegisterResponse
            {
                RunnerId = runner.RunnerId,
                Name = updated.Name,
                HeartbeatIntervalMs = (int)_options.HeartbeatInterval.TotalMilliseconds,
                ServerTime = now,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A heartbeat: renews the leases of held jobs and tells the Runner which to cancel or drop.</summary>
    public async Task<RunnerHeartbeatResponse> HeartbeatAsync(RunnerRecord runner, RunnerHeartbeatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var current = await TouchAsync(runner, now, cancellationToken).ConfigureAwait(false);
            var cancel = new List<string>();
            var lost = new List<string>();
            foreach (var held in request.ActiveJobs.Take(256))
            {
                var job = await _store.GetJobAsync(held.JobId, cancellationToken).ConfigureAwait(false);
                if (job is null || job.RunnerId != runner.RunnerId || job.LeaseId != held.LeaseId)
                {
                    lost.Add(held.JobId);
                    continue;
                }

                if (job.State is JobState.Assigned or JobState.Running)
                {
                    await _store.UpdateJobAsync(job with { LeaseExpiresAt = now + _options.LeaseDuration }, cancellationToken).ConfigureAwait(false);
                    if (job.CancelRequested)
                    {
                        cancel.Add(job.JobId);
                    }
                }
                else if (job.State != JobState.Interrupted)
                {
                    // Final already (for example cancelled before start): nothing more to do for it.
                    lost.Add(job.JobId);
                }
            }

            return new RunnerHeartbeatResponse { Cancel = cancel, Lost = lost, Paused = current.Paused };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Claims one eligible job for a Runner, waiting up to <paramref name="wait"/> for one. The claim is a conditional
    /// update from Queued, so of two Runners claiming the same job only one gets it.
    /// </summary>
    public async Task<JobAssignment?> ClaimAsync(RunnerRecord runner, TimeSpan wait, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var deadline = _time.GetUtcNow() + wait;
        while (true)
        {
            var signal = Volatile.Read(ref _work).Task;
            var assignment = await TryClaimAsync(runner, cancellationToken).ConfigureAwait(false);
            var remaining = deadline - _time.GetUtcNow();
            if (assignment is not null || remaining <= TimeSpan.Zero)
            {
                return assignment;
            }

            try
            {
                await signal.WaitAsync(remaining, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }
    }

    /// <summary>The Runner starts the job's workflow.</summary>
    public async Task<JobLeaseResponse> StartAsync(RunnerRecord runner, string jobId, JobStartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await WithLeaseAsync(runner, jobId, request.LeaseId, async (job, now) =>
        {
            await Task.CompletedTask.ConfigureAwait(false);
            if (job.State == JobState.Running)
            {
                return (null, new JobLeaseResponse { LeaseValid = true, Duplicate = true, CancelRequested = job.CancelRequested, State = job.State.ToString() });
            }

            if (job.State != JobState.Assigned)
            {
                return (null, Invalid(job));
            }

            if (job.CancelRequested)
            {
                var cancelled = Transition(job, JobState.Cancelled, now) with
                {
                    Error = new JobError(MyRPA.Workflow.Execution.ExecutionErrorCodes.Cancelled, "Cancelled before the Runner started it.", "Cancelled"),
                    LeaseExpiresAt = null,
                    Attempts = EndAttempt(job, now, "Cancelled", "Cancelled before start"),
                };
                return (cancelled, new JobLeaseResponse { LeaseValid = false, CancelRequested = true, State = nameof(JobState.Cancelled) });
            }

            var running = Transition(job, JobState.Running, now) with
            {
                StartedAt = now,
                LeaseExpiresAt = now + _options.LeaseDuration,
                Attempts = [.. job.Attempts.Select(a => a.LeaseId == job.LeaseId ? a with { StartedAt = now } : a)],
            };
            return (running, new JobLeaseResponse { LeaseValid = true, State = nameof(JobState.Running) });
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores a batch of the run's events (duplicates by attempt and sequence are ignored) and renews the lease.</summary>
    public async Task<JobLeaseResponse> AppendEventsAsync(RunnerRecord runner, string jobId, JobEventsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Events.Count > RunnerProtocol.MaxEventsPerRequest)
        {
            throw new OrchestrationException(OrchestrationFailure.Invalid, $"At most {RunnerProtocol.MaxEventsPerRequest} events per request.");
        }

        return await WithLeaseAsync(runner, jobId, request.LeaseId, async (job, now) =>
        {
            if (job.State is not (JobState.Assigned or JobState.Running or JobState.Interrupted))
            {
                return (null, Invalid(job));
            }

            var policy = await _store.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
            var attempt = job.Attempts.FirstOrDefault(a => a.LeaseId == job.LeaseId)?.Number ?? job.AttemptCount;
            var stored = request.Events
                .Where(e => e.Sequence > 0)
                .Select(e => new StoredEvent(attempt, e.Sequence, Truncate(e.Kind, 40), e.Time, JsonSerializer.Serialize(Sanitize(e, jobId), ContractsJsonContext.Default.ExecutionEventMessage)))
                .ToList();
            var room = Math.Max(0, policy.MaxEventsPerJob - job.EventCount);
            var accepted = stored.Take(room).ToList();
            var added = accepted.Count == 0 ? 0 : await _store.AppendEventsAsync(jobId, accepted, cancellationToken).ConfigureAwait(false);
            var updated = job with
            {
                EventCount = job.EventCount + added,
                EventsDropped = job.EventsDropped + (stored.Count - accepted.Count),
                LeaseExpiresAt = job.State == JobState.Interrupted ? job.LeaseExpiresAt : now + _options.LeaseDuration,
            };
            return (updated, new JobLeaseResponse { LeaseValid = true, CancelRequested = job.CancelRequested, State = job.State.ToString(), Duplicate = added == 0 && accepted.Count > 0 });
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The job's outcome. Repeating a completion is harmless (answered as a duplicate); a completion under a stale lease
    /// changes nothing. A late completion settles an interrupted job.
    /// </summary>
    public async Task<JobLeaseResponse> CompleteAsync(RunnerRecord runner, string jobId, JobCompleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!JobTransitions.TryParseOutcome(request.Status, out var outcome))
        {
            throw new OrchestrationException(OrchestrationFailure.Invalid, "The status must be Succeeded, Failed, Cancelled or TimedOut.");
        }

        return await WithLeaseAsync(runner, jobId, request.LeaseId, async (job, now) =>
        {
            await Task.CompletedTask.ConfigureAwait(false);
            if (job.IsFinal)
            {
                return (null, new JobLeaseResponse { LeaseValid = true, Duplicate = true, State = job.State.ToString() });
            }

            if (job.State == JobState.Assigned && !request.NotStarted)
            {
                // The start message was lost; the Runner did start.
                job = Transition(job, JobState.Running, now) with { StartedAt = now };
            }

            if (job.State == JobState.Assigned && outcome is not (JobState.Failed or JobState.Cancelled))
            {
                return (null, Invalid(job));
            }

            var error = request.Error is { } e ? new JobError(Truncate(e.Code, 20), Truncate(e.Message, 2000), e.ErrorType is null ? null : Truncate(e.ErrorType, 60), e.NodeId, e.ActivityType) : null;
            var outputs = request.OutputsJson is { Length: > MaxOutputsLength } ? "{\"_truncated\":\"The outputs exceeded 64 KB and were not stored.\"}"
                : IsJsonObject(request.OutputsJson) ? request.OutputsJson
                : request.OutputsJson is null ? null : "{\"_invalid\":\"The Runner sent outputs that are not a JSON object; they were not stored.\"}";
            var done = Transition(job, outcome, now) with
            {
                Error = outcome == JobState.Succeeded ? null : error ?? new JobError("MYRPA2001", $"The job ended {outcome}."),
                OutputsJson = outcome == JobState.Succeeded ? outputs : null,
                DurationMs = request.DurationMs,
                ExecutionId = request.ExecutionId is null ? null : Truncate(request.ExecutionId, 100),
                LeaseExpiresAt = null,
                Attempts = EndAttempt(job, now, outcome.ToString(), request.NotStarted ? "Not started: " + error?.Message : null),
            };
            Pulse();
            return (done, new JobLeaseResponse { LeaseValid = true, State = outcome.ToString() });
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The Runner gives back an assigned job it did not start (for example while shutting down).</summary>
    public async Task<JobLeaseResponse> ReleaseAsync(RunnerRecord runner, string jobId, JobReleaseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await WithLeaseAsync(runner, jobId, request.LeaseId, async (job, now) =>
        {
            await Task.CompletedTask.ConfigureAwait(false);
            if (job.State != JobState.Assigned)
            {
                return (null, Invalid(job));
            }

            var released = job.CancelRequested
                ? Transition(job, JobState.Cancelled, now) with { LeaseExpiresAt = null, Attempts = EndAttempt(job, now, "Cancelled", request.Reason) }
                : Requeue(job, now, "Released", Truncate(request.Reason ?? "Released by the Runner.", 200));
            Pulse();
            return (released, new JobLeaseResponse { LeaseValid = false, State = released.State.ToString() });
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Expires leases (ADR-0049): an assigned job whose Runner never started it is requeued (or fails once it ran out of
    /// attempts); a running job whose Runner stopped reporting becomes interrupted (never retried automatically).
    /// </summary>
    /// <returns>How many jobs changed.</returns>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var changed = 0;
            foreach (var job in await _store.ListActiveJobsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (job.State is not (JobState.Assigned or JobState.Running) || job.LeaseExpiresAt is not { } expires || expires > now)
                {
                    continue;
                }

                JobRecord updated;
                if (job.State == JobState.Running)
                {
                    updated = Interrupt(job, now, "The Runner stopped reporting while the job ran; the outcome is unknown.");
                }
                else if (job.CancelRequested)
                {
                    updated = Transition(job, JobState.Cancelled, now) with { LeaseExpiresAt = null, Attempts = EndAttempt(job, now, "Cancelled", "Lease expired before start") };
                }
                else if (job.Attempts.Count(a => a.Outcome == "Expired") + 1 >= job.MaxAttempts)
                {
                    updated = Transition(job, JobState.Failed, now) with
                    {
                        Error = new JobError(OrchestrationErrorCodes.NotStarted, $"The job was assigned {job.MaxAttempts} times but no Runner started it.", "NotStarted"),
                        LeaseExpiresAt = null,
                        Attempts = EndAttempt(job, now, "Expired", "Lease expired before start"),
                    };
                }
                else
                {
                    updated = Requeue(job, now, "Expired", "Lease expired before start");
                }

                if (await _store.UpdateJobAsync(updated, cancellationToken).ConfigureAwait(false))
                {
                    changed++;
                    LogLeaseExpired(_logger, job.JobId, job.State, updated.State);
                }
            }

            if (changed > 0)
            {
                Pulse();
            }

            return changed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A Runner's status now.</summary>
    public static RunnerStatus StatusOf(RunnerRecord runner, int activeJobs, DateTimeOffset now, OrchestrationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(policy);
        if (runner.Disabled)
        {
            return RunnerStatus.Disabled;
        }

        if (runner.LastSeenAt is not { } seen || now - seen > TimeSpan.FromSeconds(policy.OfflineAfterSeconds))
        {
            return RunnerStatus.Offline;
        }

        if (runner.Paused)
        {
            return RunnerStatus.Paused;
        }

        if (runner.PluginErrors.Count > 0)
        {
            return RunnerStatus.Error;
        }

        return activeJobs >= runner.Capacity ? RunnerStatus.Busy : RunnerStatus.Available;
    }

    /// <summary>Whether the Runner holds the job's current lease (the package download checks this).</summary>
    public async Task<JobRecord?> FindLeasedJobAsync(RunnerRecord runner, string jobId, string? leaseId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        var job = await _store.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        return job is { State: JobState.Assigned or JobState.Running } && job.RunnerId == runner.RunnerId && job.LeaseId == leaseId ? job : null;
    }

    private async Task<JobAssignment?> TryClaimAsync(RunnerRecord runner, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var current = await TouchAsync(runner, now, cancellationToken).ConfigureAwait(false);
            if (current.Disabled || current.Paused || current.RegisteredAt is null)
            {
                return null;
            }

            var active = await _store.ListActiveJobsAsync(cancellationToken).ConfigureAwait(false);
            if (active.Count(j => j.RunnerId == current.RunnerId && j.State is JobState.Assigned or JobState.Running) >= current.Capacity)
            {
                return null;
            }

            var candidates = active
                .Where(j => j.State == JobState.Queued && !j.CancelRequested && Ineligibility(j, current) is null)
                .OrderByDescending(j => j.Priority)
                .ThenBy(j => j.CreatedAt);
            foreach (var job in candidates)
            {
                var lease = _ids.NewId("lse");
                var number = job.AttemptCount + 1;
                var assigned = Transition(job, JobState.Assigned, now) with
                {
                    RunnerId = current.RunnerId,
                    LeaseId = lease,
                    LeaseExpiresAt = now + _options.LeaseDuration,
                    AssignedAt = now,
                    AttemptCount = number,
                    Attempts = [.. job.Attempts, new JobAttempt { Number = number, RunnerId = current.RunnerId, LeaseId = lease, AssignedAt = now }],
                };

                // Conditional on the revision read above: if anything changed the job meanwhile, try the next one.
                if (!await _store.UpdateJobAsync(assigned, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                return new JobAssignment
                {
                    JobId = job.JobId,
                    LeaseId = lease,
                    Attempt = number,
                    ProcessId = job.ProcessId,
                    ProcessName = job.ProcessName,
                    Version = job.Version,
                    PackageSha256 = job.PackageSha256,
                    EntryPath = job.EntryPath,
                    ArgumentsJson = job.ArgumentsJson,
                    TimeoutMs = job.TimeoutMs,
                    CorrelationId = job.CorrelationId,
                };
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JobLeaseResponse> WithLeaseAsync(RunnerRecord runner, string jobId, string leaseId, Func<JobRecord, DateTimeOffset, Task<(JobRecord? Updated, JobLeaseResponse Response)>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            await TouchAsync(runner, now, cancellationToken).ConfigureAwait(false);
            var job = await _store.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);

            // A Runner may only speak about jobs it holds under the lease it was given; anything else changes nothing.
            if (job is null || job.RunnerId != runner.RunnerId || job.LeaseId is null || job.LeaseId != leaseId)
            {
                return new JobLeaseResponse { LeaseValid = false, State = job?.RunnerId == runner.RunnerId ? job.State.ToString() : null };
            }

            var (updated, response) = await action(job, now).ConfigureAwait(false);
            if (updated is not null && !await _store.UpdateJobAsync(updated, cancellationToken).ConfigureAwait(false))
            {
                return new JobLeaseResponse { LeaseValid = false, State = job.State.ToString() };
            }

            return response;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RunnerRecord> TouchAsync(RunnerRecord runner, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = await _store.GetRunnerAsync(runner.RunnerId, cancellationToken).ConfigureAwait(false) ?? runner;
        var touched = current with { LastSeenAt = now };
        return await _store.UpdateRunnerAsync(touched, cancellationToken).ConfigureAwait(false) ? touched with { Revision = touched.Revision + 1 } : current;
    }

    private async Task<RunnerRecord> ChangeRunnerAsync(Actor actor, string runnerId, Func<RunnerRecord, (RunnerRecord Updated, string Action, string? Details)> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runner = await _store.GetRunnerAsync(runnerId, cancellationToken).ConfigureAwait(false)
                ?? throw new OrchestrationException(OrchestrationFailure.NotFound, $"Unknown Runner '{runnerId}'.");
            var (updated, action, details) = change(runner);
            await UpdateOrThrowAsync(_store.UpdateRunnerAsync(updated, cancellationToken)).ConfigureAwait(false);
            await AuditAsync(actor, action, runnerId, details, cancellationToken).ConfigureAwait(false);
            Pulse();
            return updated with { Revision = updated.Revision + 1 };
        }
        finally
        {
            _gate.Release();
        }
    }

    private static JobRecord Requeue(JobRecord job, DateTimeOffset now, string outcome, string reason) =>
        Transition(job, JobState.Queued, now) with
        {
            LeaseId = null,
            LeaseExpiresAt = null,
            RunnerId = null,
            Attempts = EndAttempt(job, now, outcome, reason),
        };

    private static JobRecord Interrupt(JobRecord job, DateTimeOffset now, string reason) =>
        Transition(job, JobState.Interrupted, now) with
        {
            LeaseExpiresAt = null,
            Error = new JobError(OrchestrationErrorCodes.Interrupted, reason, "Interrupted"),
            Attempts = EndAttempt(job, now, nameof(JobState.Interrupted), reason),
        };

    private static List<JobAttempt> EndAttempt(JobRecord job, DateTimeOffset now, string outcome, string? reason) =>
        [.. job.Attempts.Select(a => a.LeaseId == job.LeaseId && a.EndedAt is null ? a with { EndedAt = now, Outcome = outcome, Reason = reason } : a)];

    private static bool IsJsonObject(string? json)
    {
        if (json is null)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JobLeaseResponse Invalid(JobRecord job) => new() { LeaseValid = false, State = job.State.ToString(), CancelRequested = job.CancelRequested };

    private static ExecutionEventMessage Sanitize(ExecutionEventMessage message, string jobId) => message with
    {
        // The run id is the Runner's own; history refers to the job.
        RunId = jobId,
        Message = message.Message is null ? null : Truncate(message.Message, MaxEventMessageLength),
        Error = message.Error is { } error ? error with { Message = Truncate(error.Message, 2000) } : null,
    };
}
