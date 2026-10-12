using System.Text;
using Microsoft.Extensions.Logging;
using MyRPA.Workflow;
using MyRPA.Workflow.Validation;

namespace MyRPA.Orchestration;

/// <summary>Timing of the Runner protocol.</summary>
public sealed class OrchestratorOptions
{
    /// <summary>How often Runners send heartbeats (default 10 s).</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a lease lasts without renewal (default 45 s). Heartbeats and event batches renew it; when it runs out an
    /// assigned job is requeued (it never started) and a running job becomes <see cref="JobState.Interrupted"/>.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(45);
}

/// <summary>Why an orchestration request was refused.</summary>
public enum OrchestrationFailure
{
    /// <summary>The input is invalid.</summary>
    Invalid,

    /// <summary>Something named does not exist.</summary>
    NotFound,

    /// <summary>The current state does not allow it.</summary>
    Conflict,

    /// <summary>The actor's role does not allow it.</summary>
    Forbidden,
}

/// <summary>A refused orchestration request (the message is safe to show; it never contains secrets).</summary>
public sealed class OrchestrationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public OrchestrationException(OrchestrationFailure failure, string message)
        : base(message) => Failure = failure;

    /// <summary>Creates the exception.</summary>
    public OrchestrationException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public OrchestrationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public OrchestrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Why.</summary>
    public OrchestrationFailure Failure { get; }
}

/// <summary>Error codes of orchestration failures (MYRPA50xx).</summary>
public static class OrchestrationErrorCodes
{
    /// <summary>The Runner lacks a plugin or activity the package needs.</summary>
    public const string DependencyMissing = "MYRPA5001";

    /// <summary>The Runner stopped reporting while the job ran; the outcome is unknown.</summary>
    public const string Interrupted = "MYRPA5002";

    /// <summary>The job was assigned the maximum number of times but never started.</summary>
    public const string NotStarted = "MYRPA5003";

    /// <summary>The package failed its integrity check.</summary>
    public const string PackageIntegrity = "MYRPA5004";

    /// <summary>The Runner could not prepare the job (package unreadable, workflow invalid there).</summary>
    public const string PreparationFailed = "MYRPA5005";
}

/// <summary>
/// The orchestrator (ADR-0049): decides what runs, when and where. It never executes a workflow: Runners claim jobs
/// under leases, execute them with their own engine and report back. All state changes are serialized through one gate
/// and written with conditional (revision-checked) updates, so duplicate, stale and concurrent messages cannot move a
/// job backwards or let two Runners hold the same assignment.
/// </summary>
public sealed partial class Orchestrator : IDisposable
{
    private readonly IOrchestrationStore _store;
    private readonly IOrchestrationIds _ids;
    private readonly TimeProvider _time;
    private readonly WorkflowLoader _loader;
    private readonly OrchestratorOptions _options;
    private readonly ILogger<Orchestrator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TaskCompletionSource _work = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates the orchestrator.</summary>
    public Orchestrator(IOrchestrationStore store, IOrchestrationIds ids, TimeProvider time, WorkflowLoader loader, OrchestratorOptions options, ILogger<Orchestrator> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Protocol timing.</summary>
    public OrchestratorOptions Options => _options;

    /// <summary>The store (read-only queries go straight to it).</summary>
    public IOrchestrationStore Store => _store;

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Publishes a project as the next immutable version of its process (one process per project). The package holds
    /// the chosen entry points (the workflows a job may start) and every valid workflow of the project.
    /// </summary>
    /// <param name="actor">Who (Developer).</param>
    /// <param name="project">The project.</param>
    /// <param name="entryPaths">The entry points' project-relative paths (at least one).</param>
    /// <param name="files">The project's workflow files (relative path, content).</param>
    /// <param name="notes">Release notes.</param>
    /// <param name="pluginOf">The plugin providing an activity type (null for built-ins).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<(ProcessRecord Process, PackageRecord Package)> PublishAsync(
        Actor actor, string project, IReadOnlyList<string> entryPaths, IReadOnlyList<KeyValuePair<string, string>> files, string? notes, Func<string, PackagePlugin?> pluginOf, CancellationToken cancellationToken)
    {
        Require(actor, Role.Developer);
        ArgumentNullException.ThrowIfNull(entryPaths);
        if (string.IsNullOrWhiteSpace(project))
        {
            throw new OrchestrationException(OrchestrationFailure.Invalid, "A project is required.");
        }

        if (notes is { Length: > 2000 })
        {
            throw new OrchestrationException(OrchestrationFailure.Invalid, "Release notes are limited to 2,000 characters.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var process = await _store.FindProcessAsync(project, cancellationToken).ConfigureAwait(false);
            var isNew = process is null;
            if (process is null)
            {
                var name = Path.GetFileName(project.TrimEnd('/', '\\'));
                process = new ProcessRecord
                {
                    ProcessId = _ids.NewId("prc"),
                    Name = string.IsNullOrWhiteSpace(name) ? project : name,
                    Project = project,
                    CreatedAt = now,
                };
            }

            // The next version follows every stored one, even if an earlier publish stopped before updating the process.
            var existing = isNew ? [] : await _store.ListPackagesAsync(process.ProcessId, cancellationToken).ConfigureAwait(false);
            var version = Math.Max(process.LatestVersion, existing.Count == 0 ? 0 : existing.Max(p => p.Version)) + 1;
            var built = WorkflowPackage.Build(
                new PackageBuildRequest
                {
                    ProcessId = process.ProcessId,
                    Name = process.Name,
                    Version = version,
                    Project = project,
                    EntryPaths = entryPaths,
                    Files = files,
                    PublishedAt = now,
                    PublishedBy = actor.Name,
                    Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
                },
                _loader.Load,
                pluginOf);
            if (!built.Succeeded)
            {
                throw new OrchestrationException(OrchestrationFailure.Invalid, "The project cannot be published: " + string.Join(" ", built.Errors.Take(10)));
            }

            if (isNew && !await _store.InsertProcessAsync(process, cancellationToken).ConfigureAwait(false))
            {
                throw new OrchestrationException(OrchestrationFailure.Conflict, "The process could not be created.");
            }

            var package = new PackageRecord { ProcessId = process.ProcessId, Version = version, Sha256 = built.Sha256, Size = built.Content.Length, Manifest = built.Manifest! };
            if (!await _store.InsertPackageAsync(package, built.Content, cancellationToken).ConfigureAwait(false))
            {
                throw new OrchestrationException(OrchestrationFailure.Conflict, $"Version {version} already exists.");
            }

            var current = await _store.GetProcessAsync(process.ProcessId, cancellationToken).ConfigureAwait(false) ?? process;
            var updated = current with { LatestVersion = version };
            await UpdateOrThrowAsync(_store.UpdateProcessAsync(updated, cancellationToken)).ConfigureAwait(false);
            await AuditAsync(actor, "package.publish", process.ProcessId, $"{process.Name} v{version} ({built.Sha256[..12]})", cancellationToken).ConfigureAwait(false);
            LogPublished(_logger, process.ProcessId, version);
            return (updated with { Revision = updated.Revision + 1 }, package);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Deploys a published version: jobs and schedules use it from now on. Deploying an earlier version is a rollback;
    /// published versions never change.
    /// </summary>
    public async Task<ProcessRecord> DeployAsync(Actor actor, string processId, int version, CancellationToken cancellationToken)
    {
        Require(actor, Role.Developer);
        return await ChangeProcessAsync(actor, processId, async process =>
        {
            if (await _store.GetPackageAsync(processId, version, cancellationToken).ConfigureAwait(false) is null)
            {
                throw new OrchestrationException(OrchestrationFailure.NotFound, $"Version {version} of the process does not exist.");
            }

            var action = process.DeployedVersion is { } previous && version < previous ? "process.rollback" : "process.deploy";
            var details = process.DeployedVersion is { } was ? $"v{version} (was v{was})" : $"v{version}";
            return (process with { DeployedVersion = version, DeployedAt = _time.GetUtcNow(), DeployedBy = actor.Name }, action, details);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a process's deployment (it stays published; jobs need an explicit version).</summary>
    public Task<ProcessRecord> UndeployAsync(Actor actor, string processId, CancellationToken cancellationToken)
    {
        Require(actor, Role.Developer);
        return ChangeProcessAsync(actor, processId, process =>
            Task.FromResult((process with { DeployedVersion = null, DeployedAt = _time.GetUtcNow(), DeployedBy = actor.Name }, "process.undeploy", (string?)null)), cancellationToken);
    }

    /// <summary>Enables or disables a deployment (disabled: no manual or scheduled jobs are accepted).</summary>
    public Task<ProcessRecord> SetEnabledAsync(Actor actor, string processId, bool enabled, CancellationToken cancellationToken)
    {
        Require(actor, Role.Developer);
        return ChangeProcessAsync(actor, processId, process =>
            Task.FromResult((process with { Enabled = enabled }, enabled ? "process.enable" : "process.disable", (string?)null)), cancellationToken);
    }

    /// <summary>Sets where a process's jobs run and their defaults.</summary>
    public Task<ProcessRecord> ConfigureAsync(Actor actor, string processId, string? targetGroup, string? targetRunnerId, int priority, int? timeoutMs, CancellationToken cancellationToken)
    {
        Require(actor, Role.Developer);
        if (timeoutMs is <= 0 || priority is < -100 or > 100)
        {
            throw new OrchestrationException(OrchestrationFailure.Invalid, "Priority must be -100 to 100 and the time limit positive.");
        }

        return ChangeProcessAsync(actor, processId, async process =>
        {
            if (targetRunnerId is not null && await _store.GetRunnerAsync(targetRunnerId, cancellationToken).ConfigureAwait(false) is null)
            {
                throw new OrchestrationException(OrchestrationFailure.NotFound, $"Unknown Runner '{targetRunnerId}'.");
            }

            var group = string.IsNullOrWhiteSpace(targetGroup) ? null : targetGroup.Trim();
            return (process with { TargetGroup = group, TargetRunnerId = targetRunnerId, Priority = priority, TimeoutMs = timeoutMs },
                "process.configure", $"group={group ?? "any"}, runner={targetRunnerId ?? "any"}, priority={priority}");
        }, cancellationToken);
    }

    /// <summary>Returns an entry-point workflow of a published version (verified against its hash).</summary>
    public async Task<(PackageRecord Package, WorkflowDefinition Workflow)> LoadEntryAsync(string processId, int version, string entryPath, CancellationToken cancellationToken)
    {
        var package = await _store.GetPackageAsync(processId, version, cancellationToken).ConfigureAwait(false)
            ?? throw new OrchestrationException(OrchestrationFailure.NotFound, $"Version {version} of the process does not exist.");
        var content = await _store.ReadPackageContentAsync(processId, version, cancellationToken).ConfigureAwait(false);
        PackageDocument? document = null;
        string? error = "It is missing.";
        if (content is null || !WorkflowPackage.TryVerify(content, package.Sha256, out document, out error))
        {
            throw new OrchestrationException(OrchestrationFailure.Conflict, $"The stored package of v{version} failed its integrity check. {error}");
        }

        if (WorkflowPackage.FindEntryPoint(document!.Manifest, entryPath) is null)
        {
            throw new OrchestrationException(OrchestrationFailure.Invalid, $"v{version} has no entry point '{entryPath}'.");
        }

        var entry = document.Files.First(f => f.Path == entryPath);
        var workflow = _loader.Load(entry.Content).Workflow
            ?? throw new OrchestrationException(OrchestrationFailure.Conflict, $"v{version} no longer validates on this Server (are its plugins still loaded?).");
        return (package, workflow);
    }

    /// <summary>Throws <see cref="OrchestrationFailure.Forbidden"/> unless <paramref name="actor"/> has at least <paramref name="role"/>.</summary>
    public static void Require(Actor actor, Role role)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.Has(role))
        {
            throw new OrchestrationException(OrchestrationFailure.Forbidden, $"This needs the {role} role; '{actor.Name}' is {actor.Role}.");
        }
    }

    private async Task<ProcessRecord> ChangeProcessAsync(Actor actor, string processId, Func<ProcessRecord, Task<(ProcessRecord Updated, string Action, string? Details)>> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var process = await _store.GetProcessAsync(processId, cancellationToken).ConfigureAwait(false)
                ?? throw new OrchestrationException(OrchestrationFailure.NotFound, $"Unknown process '{processId}'.");
            var (updated, action, details) = await change(process).ConfigureAwait(false);
            await UpdateOrThrowAsync(_store.UpdateProcessAsync(updated, cancellationToken)).ConfigureAwait(false);
            await AuditAsync(actor, action, processId, details, cancellationToken).ConfigureAwait(false);
            return updated with { Revision = updated.Revision + 1 };
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task AuditAsync(Actor actor, string action, string? target, string? details, CancellationToken cancellationToken) =>
        _store.AppendAuditAsync(_time.GetUtcNow(), actor.Name, action, target, details, cancellationToken);

    private static async Task UpdateOrThrowAsync(Task<bool> update)
    {
        if (!await update.ConfigureAwait(false))
        {
            // Only reachable if another process writes the same store; this one serializes its own writes.
            throw new OrchestrationException(OrchestrationFailure.Conflict, "The record changed while it was being updated; try again.");
        }
    }

    /// <summary>Wakes claim requests waiting for work.</summary>
    private void Pulse() => Interlocked.Exchange(ref _work, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    [LoggerMessage(EventId = 5000, Level = LogLevel.Information, Message = "Published {ProcessId} v{Version}")]
    private static partial void LogPublished(ILogger logger, string processId, int version);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "Runner {RunnerId} registered ({Activities} activities)")]
    private static partial void LogRegistered(ILogger logger, string runnerId, int activities);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning, Message = "Job {JobId}: lease expired while {State}; now {NewState}")]
    private static partial void LogLeaseExpired(ILogger logger, string jobId, JobState state, JobState newState);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Information, Message = "Schedule {ScheduleId} fired for {FireTime}: {Outcome}")]
    private static partial void LogScheduleFired(ILogger logger, string scheduleId, DateTimeOffset fireTime, string outcome);

    private static string Truncate(string? text, int max) =>
        text is null ? string.Empty : text.Length <= max ? text : new StringBuilder(text, 0, max, max + 1).Append('…').ToString();
}
