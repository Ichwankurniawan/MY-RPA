using System.Text.Json;

namespace MyRPA.Orchestration;

/// <summary>A schedule definition from a client.</summary>
public sealed record ScheduleDefinition
{
    /// <summary>Name.</summary>
    public required string Name { get; init; }

    /// <summary>Kind.</summary>
    public required ScheduleKind Kind { get; init; }

    /// <summary>The entry point to start (null: the deployed package's only entry point).</summary>
    public string? EntryPath { get; init; }

    /// <summary>Cron expression.</summary>
    public string? Cron { get; init; }

    /// <summary>Interval in minutes.</summary>
    public int? IntervalMinutes { get; init; }

    /// <summary>Start (or only) time.</summary>
    public DateTimeOffset? StartAt { get; init; }

    /// <summary>Time zone id.</summary>
    public required string TimeZone { get; init; }

    /// <summary>Missed fire times.</summary>
    public MisfirePolicy Misfire { get; init; }

    /// <summary>Overlap.</summary>
    public OverlapPolicy Overlap { get; init; }

    /// <summary>Job arguments as JSON values.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Arguments { get; init; }

    /// <summary>Enabled.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>Schedules (ADR-0049).</summary>
public sealed partial class Orchestrator
{
    /// <summary>Most schedules per Server.</summary>
    private const int MaxSchedules = 500;

    /// <summary>Creates a schedule for a process's deployed version.</summary>
    public async Task<ScheduleRecord> CreateScheduleAsync(Actor actor, string processId, ScheduleDefinition definition, CancellationToken cancellationToken)
    {
        Require(actor, Role.Developer);
        ArgumentNullException.ThrowIfNull(definition);
        var process = await _store.GetProcessAsync(processId, cancellationToken).ConfigureAwait(false)
            ?? throw new OrchestrationException(OrchestrationFailure.NotFound, $"Unknown process '{processId}'.");
        if ((await _store.ListSchedulesAsync(cancellationToken).ConfigureAwait(false)).Count >= MaxSchedules)
        {
            throw new OrchestrationException(OrchestrationFailure.Conflict, $"A Server holds at most {MaxSchedules} schedules.");
        }

        var (entryPath, arguments) = await CheckScheduleArgumentsAsync(process, definition, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var schedule = Define(new ScheduleRecord
        {
            ScheduleId = _ids.NewId("sch"),
            ProcessId = processId,
            EntryPath = entryPath,
            Name = definition.Name,
            Kind = definition.Kind,
            TimeZone = definition.TimeZone,
            CreatedAt = now,
            CreatedBy = actor.Name,
        }, definition, entryPath, arguments, now);
        await _store.InsertScheduleAsync(schedule, cancellationToken).ConfigureAwait(false);
        await AuditAsync(actor, "schedule.create", schedule.ScheduleId, $"{schedule.Name} for {process.Name}: {Describe(schedule)}", cancellationToken).ConfigureAwait(false);
        return schedule;
    }

    /// <summary>Changes a schedule's definition (the next fire time is recomputed from now).</summary>
    public async Task<ScheduleRecord> UpdateScheduleAsync(Actor actor, string scheduleId, ScheduleDefinition definition, CancellationToken cancellationToken)
    {
        Require(actor, Role.Developer);
        ArgumentNullException.ThrowIfNull(definition);
        var existing = await _store.GetScheduleAsync(scheduleId, cancellationToken).ConfigureAwait(false)
            ?? throw new OrchestrationException(OrchestrationFailure.NotFound, $"Unknown schedule '{scheduleId}'.");
        var process = await _store.GetProcessAsync(existing.ProcessId, cancellationToken).ConfigureAwait(false)
            ?? throw new OrchestrationException(OrchestrationFailure.NotFound, "The schedule's process no longer exists.");
        var (entryPath, arguments) = await CheckScheduleArgumentsAsync(process, definition, cancellationToken).ConfigureAwait(false);
        return await ChangeScheduleAsync(actor, scheduleId, current =>
        {
            var updated = Define(current, definition, entryPath, arguments, _time.GetUtcNow());
            return (updated, "schedule.update", Describe(updated));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Enables or disables a schedule. Enabling continues from now: fire times missed while disabled are not run.</summary>
    public Task<ScheduleRecord> SetScheduleEnabledAsync(Actor actor, string scheduleId, bool enabled, CancellationToken cancellationToken)
    {
        Require(actor, Role.Operator);
        return ChangeScheduleAsync(actor, scheduleId, schedule =>
        {
            var now = _time.GetUtcNow();
            var updated = schedule with { Enabled = enabled, NextFireAt = enabled ? ScheduleTimes.Next(schedule, now) : schedule.NextFireAt };
            return (updated, enabled ? "schedule.enable" : "schedule.disable", null);
        }, cancellationToken);
    }

    /// <summary>Deletes a schedule (the jobs it created stay).</summary>
    public async Task DeleteScheduleAsync(Actor actor, string scheduleId, CancellationToken cancellationToken)
    {
        Require(actor, Role.Developer);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var schedule = await _store.GetScheduleAsync(scheduleId, cancellationToken).ConfigureAwait(false)
                ?? throw new OrchestrationException(OrchestrationFailure.NotFound, $"Unknown schedule '{scheduleId}'.");
            await _store.DeleteScheduleAsync(scheduleId, cancellationToken).ConfigureAwait(false);
            await AuditAsync(actor, "schedule.delete", scheduleId, schedule.Name, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Fires every due schedule (ADR-0049). Each fire time creates at most one job: the job's idempotency key is the
    /// schedule and fire time, and the job and the schedule's new fire times are written in one transaction, so repeated
    /// evaluation and restarts never duplicate a job. A fire time missed by more than
    /// <see cref="ScheduleTimes.MisfireGrace"/> follows the schedule's misfire policy.
    /// </summary>
    /// <returns>Jobs created.</returns>
    public async Task<int> EvaluateSchedulesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var created = 0;
        try
        {
            var now = _time.GetUtcNow();
            foreach (var schedule in await _store.ListSchedulesAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!schedule.Enabled || schedule.NextFireAt is not { } due || due > now)
                {
                    continue;
                }

                var (job, outcome, fireTime) = await PlanFireAsync(schedule, due, now, cancellationToken).ConfigureAwait(false);
                var next = ScheduleTimes.Next(schedule, now);
                var fired = schedule with { LastFireAt = fireTime, NextFireAt = next, LastOutcome = outcome };
                if (await _store.FireScheduleAsync(fired, job, cancellationToken).ConfigureAwait(false))
                {
                    created += job is null ? 0 : 1;
                }
                else if (job is not null)
                {
                    // The job for this fire time exists already (an earlier evaluation): just advance the schedule.
                    var current = await _store.GetScheduleAsync(schedule.ScheduleId, cancellationToken).ConfigureAwait(false);
                    if (current is not null)
                    {
                        await _store.FireScheduleAsync(current with { LastFireAt = fireTime, NextFireAt = next, LastOutcome = "Job already created for this fire time" }, null, cancellationToken).ConfigureAwait(false);
                    }
                }

                LogScheduleFired(_logger, schedule.ScheduleId, fireTime, outcome);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (created > 0)
        {
            Pulse();
        }

        return created;
    }

    private async Task<(JobRecord? Job, string Outcome, DateTimeOffset FireTime)> PlanFireAsync(ScheduleRecord schedule, DateTimeOffset due, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var fireTime = due;
        if (now - due > ScheduleTimes.MisfireGrace)
        {
            if (schedule.Misfire == MisfirePolicy.Skip)
            {
                return (null, $"Skipped: the fire time {due:u} was missed (misfire policy Skip).", due);
            }

            // RunOnce: one job, for the latest missed fire time.
            for (var i = 0; i < 100_000 && ScheduleTimes.Next(schedule, fireTime) is { } later && later <= now; i++)
            {
                fireTime = later;
            }
        }

        var process = await _store.GetProcessAsync(schedule.ProcessId, cancellationToken).ConfigureAwait(false);
        if (process is null || !process.Enabled || process.DeployedVersion is not { } version)
        {
            return (null, process is null ? "Skipped: the process no longer exists." : !process.Enabled ? "Skipped: the deployment is disabled." : "Skipped: no version is deployed.", fireTime);
        }

        if (schedule.Overlap == OverlapPolicy.Skip
            && (await _store.ListActiveJobsAsync(cancellationToken).ConfigureAwait(false)).Any(j => j.ScheduleId == schedule.ScheduleId))
        {
            return (null, "Skipped: a job from this schedule is still active (overlap policy Skip).", fireTime);
        }

        try
        {
            var (package, workflow) = await LoadEntryAsync(process.ProcessId, version, schedule.EntryPath, cancellationToken).ConfigureAwait(false);
            var arguments = ValidateArguments(workflow, ParseArguments(schedule.ArgumentsJson), null);
            var policy = await _store.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
            var job = NewJob(process, package, schedule.EntryPath, arguments, $"{Actor.Scheduler.Name} ({schedule.Name})", policy) with
            {
                Priority = process.Priority,
                TargetGroup = process.TargetGroup,
                TargetRunnerId = process.TargetRunnerId,
                TimeoutMs = process.TimeoutMs ?? policy.DefaultTimeoutMinutes * 60_000,
                Source = "schedule",
                ScheduleId = schedule.ScheduleId,
                ScheduledFor = fireTime,
                IdempotencyKey = $"schedule:{schedule.ScheduleId}:{fireTime.UtcTicks}",
            };
            return (job, $"Created job {job.JobId} (v{version}).", fireTime);
        }
        catch (OrchestrationException ex)
        {
            return (null, $"Skipped: {ex.Message}", fireTime);
        }
    }

    private async Task<(string EntryPath, string Arguments)> CheckScheduleArgumentsAsync(ProcessRecord process, ScheduleDefinition definition, CancellationToken cancellationToken)
    {
        var version = process.DeployedVersion ?? process.LatestVersion;
        if (version == 0)
        {
            throw new OrchestrationException(OrchestrationFailure.Conflict, $"'{process.Name}' has no published version to schedule.");
        }

        var entryPath = string.IsNullOrWhiteSpace(definition.EntryPath)
            ? await SoleEntryPointAsync(process, version, cancellationToken).ConfigureAwait(false)
            : definition.EntryPath;
        var (_, workflow) = await LoadEntryAsync(process.ProcessId, version, entryPath, cancellationToken).ConfigureAwait(false);
        return (entryPath, ValidateArguments(workflow, definition.Arguments, null));
    }

    private static ScheduleRecord Define(ScheduleRecord schedule, ScheduleDefinition definition, string entryPath, string arguments, DateTimeOffset now)
    {
        var name = (definition.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 80)
        {
            throw new OrchestrationException(OrchestrationFailure.Invalid, "A schedule needs a name of up to 80 characters.");
        }

        var updated = schedule with
        {
            Name = name,
            EntryPath = entryPath,
            Kind = definition.Kind,
            Cron = definition.Kind == ScheduleKind.Cron ? definition.Cron?.Trim() : null,
            IntervalMinutes = definition.Kind == ScheduleKind.Interval ? definition.IntervalMinutes : null,
            StartAt = definition.StartAt,
            TimeZone = definition.TimeZone,
            Misfire = definition.Misfire,
            Overlap = definition.Overlap,
            ArgumentsJson = arguments,
            Enabled = definition.Enabled,
        };
        if (ScheduleTimes.Validate(updated) is { } error)
        {
            throw new OrchestrationException(OrchestrationFailure.Invalid, error);
        }

        return updated with { NextFireAt = ScheduleTimes.Next(updated, now) };
    }

    private async Task<ScheduleRecord> ChangeScheduleAsync(Actor actor, string scheduleId, Func<ScheduleRecord, (ScheduleRecord Updated, string Action, string? Details)> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var schedule = await _store.GetScheduleAsync(scheduleId, cancellationToken).ConfigureAwait(false)
                ?? throw new OrchestrationException(OrchestrationFailure.NotFound, $"Unknown schedule '{scheduleId}'.");
            var (updated, action, details) = change(schedule);
            await UpdateOrThrowAsync(_store.UpdateScheduleAsync(updated, cancellationToken)).ConfigureAwait(false);
            await AuditAsync(actor, action, scheduleId, details ?? schedule.Name, cancellationToken).ConfigureAwait(false);
            return updated with { Revision = updated.Revision + 1 };
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Dictionary<string, JsonElement> ParseArguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    private static string Describe(ScheduleRecord schedule) => schedule.Kind switch
    {
        ScheduleKind.Once => $"once at {schedule.StartAt:u}",
        ScheduleKind.Interval => $"every {schedule.IntervalMinutes} min",
        _ => $"cron '{schedule.Cron}' ({schedule.TimeZone})",
    };
}
