using System.Text;
using System.Text.Json;
using MyRPA.Workflow;
using MyRPA.Workflow.Values;

namespace MyRPA.Orchestration;

/// <summary>A job submission.</summary>
public sealed record SubmitJobRequest
{
    /// <summary>The process.</summary>
    public required string ProcessId { get; init; }

    /// <summary>An exact version (null: the deployed version at submission, pinned in the job).</summary>
    public int? Version { get; init; }

    /// <summary>The entry point to start (null: the package's only entry point).</summary>
    public string? EntryPath { get; init; }

    /// <summary>Input arguments as JSON values.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Arguments { get; init; }

    /// <summary>Input arguments as text typed by a person (parsed like the CLI's <c>--arg</c>).</summary>
    public IReadOnlyDictionary<string, string>? ArgumentText { get; init; }

    /// <summary>Priority (null: the process default).</summary>
    public int? Priority { get; init; }

    /// <summary>A specific Runner (null: the process target).</summary>
    public string? TargetRunnerId { get; init; }

    /// <summary>A Runner group (null: the process target).</summary>
    public string? TargetGroup { get; init; }

    /// <summary>Time limit (null: the process default).</summary>
    public int? TimeoutMs { get; init; }

    /// <summary>Client key: a repeated submission with the same key returns the first job instead of creating another.</summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>Jobs: submission, cancellation, retry (ADR-0049).</summary>
public sealed partial class Orchestrator
{
    /// <summary>
    /// Accepts a job for an exact version and persists it before returning. With an idempotency key, a repeated
    /// submission returns the job the key first created (<c>Created</c> false).
    /// </summary>
    public async Task<(JobRecord Job, bool Created)> SubmitAsync(Actor actor, SubmitJobRequest request, CancellationToken cancellationToken)
    {
        Require(actor, Role.Operator);
        ArgumentNullException.ThrowIfNull(request);
        if (request.IdempotencyKey is { } key && (key.Length is 0 or > 200))
        {
            throw new OrchestrationException(OrchestrationFailure.Invalid, "An idempotency key has 1 to 200 characters.");
        }

        if (request.IdempotencyKey is not null
            && await _store.FindJobByIdempotencyKeyAsync(request.IdempotencyKey, cancellationToken).ConfigureAwait(false) is { } earlier)
        {
            return earlier.ProcessId == request.ProcessId
                ? (earlier, false)
                : throw new OrchestrationException(OrchestrationFailure.Conflict, "The idempotency key was already used for another process.");
        }

        var process = await _store.GetProcessAsync(request.ProcessId, cancellationToken).ConfigureAwait(false)
            ?? throw new OrchestrationException(OrchestrationFailure.NotFound, $"Unknown process '{request.ProcessId}'.");
        if (!process.Enabled)
        {
            throw new OrchestrationException(OrchestrationFailure.Conflict, $"The deployment of '{process.Name}' is disabled.");
        }

        var version = request.Version ?? process.DeployedVersion
            ?? throw new OrchestrationException(OrchestrationFailure.Conflict, $"'{process.Name}' is published but not deployed; deploy a version or name one.");
        var entryPath = request.EntryPath ?? await SoleEntryPointAsync(process, version, cancellationToken).ConfigureAwait(false);
        var (package, workflow) = await LoadEntryAsync(process.ProcessId, version, entryPath, cancellationToken).ConfigureAwait(false);
        var arguments = ValidateArguments(workflow, request.Arguments, request.ArgumentText);
        var targetRunner = request.TargetRunnerId ?? process.TargetRunnerId;
        if (targetRunner is not null && await _store.GetRunnerAsync(targetRunner, cancellationToken).ConfigureAwait(false) is not { Disabled: false })
        {
            throw new OrchestrationException(OrchestrationFailure.Invalid, $"Runner '{targetRunner}' does not exist or is revoked.");
        }

        if (request.TimeoutMs is <= 0 || request.Priority is < -100 or > 100)
        {
            throw new OrchestrationException(OrchestrationFailure.Invalid, "Priority must be -100 to 100 and the time limit positive.");
        }

        var policy = await _store.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        var job = NewJob(process, package, entryPath, arguments, actor.Name, policy) with
        {
            Priority = request.Priority ?? process.Priority,
            TargetRunnerId = targetRunner,
            TargetGroup = string.IsNullOrWhiteSpace(request.TargetGroup) ? process.TargetGroup : request.TargetGroup.Trim(),
            TimeoutMs = request.TimeoutMs ?? process.TimeoutMs ?? policy.DefaultTimeoutMinutes * 60_000,
            IdempotencyKey = request.IdempotencyKey,
        };
        return await InsertJobAsync(job, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Requests cancellation. A queued job is cancelled at once; an assigned or running one when its Runner reports
    /// (cooperative: the engine stops at the next activity boundary, and an external operation in progress may finish).
    /// </summary>
    public async Task<JobRecord> CancelAsync(Actor actor, string jobId, CancellationToken cancellationToken)
    {
        Require(actor, Role.Operator);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = await GetJobOrThrowAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (job.IsFinal || job.State == JobState.Interrupted)
            {
                throw new OrchestrationException(OrchestrationFailure.Conflict, job.State == JobState.Interrupted
                    ? "The job's outcome is unknown (its Runner was lost); there is nothing to cancel."
                    : $"The job has already finished ({job.State}).");
            }

            var now = _time.GetUtcNow();
            var updated = job.State == JobState.Queued
                ? Transition(job, JobState.Cancelled, now) with { CancelRequested = true, CancelRequestedBy = actor.Name, Error = new JobError(MyRPA.Workflow.Execution.ExecutionErrorCodes.Cancelled, "Cancelled before it was assigned to a Runner.", "Cancelled") }
                : job with { CancelRequested = true, CancelRequestedBy = actor.Name };
            await UpdateOrThrowAsync(_store.UpdateJobAsync(updated, cancellationToken)).ConfigureAwait(false);
            return updated with { Revision = updated.Revision + 1 };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Submits a new job that repeats a finished or interrupted one (same exact version and arguments). Retrying is an
    /// operator's decision: the first job's side effects may already have happened.
    /// </summary>
    public async Task<(JobRecord Job, bool Created)> RetryAsync(Actor actor, string jobId, string? idempotencyKey, CancellationToken cancellationToken)
    {
        Require(actor, Role.Operator);
        if (idempotencyKey is not null && await _store.FindJobByIdempotencyKeyAsync(idempotencyKey, cancellationToken).ConfigureAwait(false) is { } earlier)
        {
            return (earlier, false);
        }

        var original = await GetJobOrThrowAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (original.IsActive)
        {
            throw new OrchestrationException(OrchestrationFailure.Conflict, "The job has not finished yet.");
        }

        var process = await _store.GetProcessAsync(original.ProcessId, cancellationToken).ConfigureAwait(false)
            ?? throw new OrchestrationException(OrchestrationFailure.NotFound, "The job's process no longer exists.");
        if (!process.Enabled)
        {
            throw new OrchestrationException(OrchestrationFailure.Conflict, $"The deployment of '{process.Name}' is disabled.");
        }

        var package = await _store.GetPackageAsync(original.ProcessId, original.Version, cancellationToken).ConfigureAwait(false)
            ?? throw new OrchestrationException(OrchestrationFailure.NotFound, "The job's version no longer exists.");
        var policy = await _store.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        var job = NewJob(process, package, original.EntryPath, original.ArgumentsJson, actor.Name, policy) with
        {
            Priority = original.Priority,
            TargetRunnerId = original.TargetRunnerId,
            TargetGroup = original.TargetGroup,
            TimeoutMs = original.TimeoutMs,
            Source = "retry",
            RetryOfJobId = original.JobId,
            IdempotencyKey = idempotencyKey,
        };
        return await InsertJobAsync(job, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Why a queued job is waiting: which Runners could take it, and what the others lack.</summary>
    public async Task<IReadOnlyList<string>> ExplainWaitAsync(JobRecord job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var runners = await _store.ListRunnersAsync(cancellationToken).ConfigureAwait(false);
        var policy = await _store.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var reasons = new List<string>();
        foreach (var runner in runners)
        {
            var missing = Ineligibility(job, runner);
            var status = StatusOf(runner, 0, now, policy);
            reasons.Add(missing is null
                ? $"{runner.Name}: eligible ({status})"
                : $"{runner.Name}: {missing}");
        }

        return reasons.Count == 0 ? ["No Runner is registered."] : reasons;
    }

    /// <summary>Why <paramref name="runner"/> cannot take <paramref name="job"/>, or null when it can (dispatch eligibility).</summary>
    public static string? Ineligibility(JobRecord job, RunnerRecord runner)
    {
        if (runner.Disabled)
        {
            return "revoked";
        }

        if (job.TargetRunnerId is { } target && target != runner.RunnerId)
        {
            return "not the targeted Runner";
        }

        if (job.TargetGroup is { } group && !string.Equals(group, runner.Group, StringComparison.OrdinalIgnoreCase))
        {
            return $"not in group '{group}'";
        }

        var activities = runner.Activities.ToHashSet(StringComparer.Ordinal);
        var missingActivities = job.RequiredActivities.Where(a => !activities.Contains(a)).ToList();
        if (missingActivities.Count > 0)
        {
            return "lacks " + string.Join(", ", missingActivities.Take(5));
        }

        foreach (var plugin in job.RequiredPlugins)
        {
            var installed = runner.Plugins.FirstOrDefault(p => p.Id == plugin.Id);
            if (installed is null)
            {
                return $"lacks plugin {plugin.Id}";
            }

            if (!WorkflowPackage.Satisfies(installed.Version, plugin.Version))
            {
                return $"has {plugin.Id} {installed.Version}, needs {plugin.Version} or later";
            }
        }

        return null;
    }

    internal static string ValidateArguments(WorkflowDefinition workflow, IReadOnlyDictionary<string, JsonElement>? json, IReadOnlyDictionary<string, string>? text)
    {
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in json ?? new Dictionary<string, JsonElement>())
        {
            var definition = InputArgument(workflow, name);
            if (!WorkflowValues.TryFromJson(value, definition.Type, out var converted, out var error))
            {
                throw new OrchestrationException(OrchestrationFailure.Invalid, $"Argument '{name}': {error}");
            }

            values[name] = converted;
        }

        foreach (var (name, value) in text ?? new Dictionary<string, string>())
        {
            var definition = InputArgument(workflow, name);
            if (values.ContainsKey(name))
            {
                throw new OrchestrationException(OrchestrationFailure.Invalid, $"Argument '{name}' is given both as a value and as text.");
            }

            if (!WorkflowValues.TryParseText(value ?? string.Empty, definition.Type, out var converted, out var error))
            {
                throw new OrchestrationException(OrchestrationFailure.Invalid, $"Argument '{name}': {error}");
            }

            values[name] = converted;
        }

        var missing = workflow.Arguments.Where(a => a.IsInput && a.IsRequired && a.DefaultValue is null && !values.ContainsKey(a.Name)).Select(a => a.Name).ToList();
        if (missing.Count > 0)
        {
            throw new OrchestrationException(OrchestrationFailure.Invalid, "Required arguments are missing: " + string.Join(", ", missing) + ".");
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WorkflowValues.WriteJson(writer, WorkflowValues.Dictionary(values));
        }

        var result = Encoding.UTF8.GetString(buffer.ToArray());
        return result.Length > 256 * 1024
            ? throw new OrchestrationException(OrchestrationFailure.Invalid, "Job arguments are limited to 256 KB.")
            : result;
    }

    private static ArgumentDefinition InputArgument(WorkflowDefinition workflow, string name)
    {
        var definition = workflow.Arguments.FirstOrDefault(a => a.Name == name && a.IsInput)
            ?? throw new OrchestrationException(OrchestrationFailure.Invalid, $"'{name}' is not an input argument of the workflow.");

        // Job arguments are stored in the job history; secrets come from the Runner's own configuration instead.
        return workflow.SecretNames.Contains(name)
            ? throw new OrchestrationException(OrchestrationFailure.Invalid, $"'{name}' feeds a secret property and cannot be stored with a job.")
            : definition;
    }

    private async Task<string> SoleEntryPointAsync(ProcessRecord process, int version, CancellationToken cancellationToken)
    {
        var package = await _store.GetPackageAsync(process.ProcessId, version, cancellationToken).ConfigureAwait(false)
            ?? throw new OrchestrationException(OrchestrationFailure.NotFound, $"Version {version} of the process does not exist.");
        return package.Manifest.EntryPoints is [var only]
            ? only.Path
            : throw new OrchestrationException(OrchestrationFailure.Invalid, $"v{version} of '{process.Name}' has {package.Manifest.EntryPoints.Count} entry points; name the one to start.");
    }

    private JobRecord NewJob(ProcessRecord process, PackageRecord package, string entryPath, string argumentsJson, string submittedBy, OrchestrationPolicy policy)
    {
        var id = _ids.NewId("job");
        return new JobRecord
        {
            JobId = id,
            ProcessId = process.ProcessId,
            ProcessName = process.Name,
            Version = package.Version,
            PackageSha256 = package.Sha256,
            EntryPath = entryPath,
            ArgumentsJson = argumentsJson,
            RequiredActivities = package.Manifest.Activities,
            RequiredPlugins = package.Manifest.Plugins,
            State = JobState.Queued,
            CreatedAt = _time.GetUtcNow(),
            CorrelationId = id,
            SubmittedBy = submittedBy,
            MaxAttempts = policy.MaxAttempts,
        };
    }

    private async Task<(JobRecord Job, bool Created)> InsertJobAsync(JobRecord job, CancellationToken cancellationToken)
    {
        if (!await _store.InsertJobAsync(job, cancellationToken).ConfigureAwait(false))
        {
            // Lost a race with the same idempotency key: return the job that won.
            if (job.IdempotencyKey is not null && await _store.FindJobByIdempotencyKeyAsync(job.IdempotencyKey, cancellationToken).ConfigureAwait(false) is { } winner)
            {
                return (winner, false);
            }

            throw new OrchestrationException(OrchestrationFailure.Conflict, "The job could not be stored.");
        }

        Pulse();
        return (job, true);
    }

    private async Task<JobRecord> GetJobOrThrowAsync(string jobId, CancellationToken cancellationToken) =>
        await _store.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new OrchestrationException(OrchestrationFailure.NotFound, $"Unknown job '{jobId}'.");

    /// <summary>Applies a state change, refusing illegal ones (the state machine is the only way state changes).</summary>
    private static JobRecord Transition(JobRecord job, JobState to, DateTimeOffset now)
    {
        if (!JobTransitions.IsAllowed(job.State, to))
        {
            throw new OrchestrationException(OrchestrationFailure.Conflict, $"A job cannot go from {job.State} to {to}.");
        }

        return job with
        {
            State = to,
            CompletedAt = JobTransitions.IsFinal(to) || to == JobState.Interrupted ? now : job.CompletedAt,
        };
    }
}
