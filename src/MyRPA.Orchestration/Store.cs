using System.Text.Json.Serialization;

namespace MyRPA.Orchestration;

/// <summary>
/// Durable orchestration state (ADR-0050). Each method is atomic. Updates of revisioned records are conditional: they
/// succeed only when the stored revision equals the record's <c>Revision</c>, and store it incremented, so a stale
/// writer can never overwrite a newer state. Published packages can be inserted but never changed.
/// </summary>
public interface IOrchestrationStore : IAsyncDisposable
{
    /// <summary>Creates or upgrades the storage.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>A process.</summary>
    Task<ProcessRecord?> GetProcessAsync(string processId, CancellationToken cancellationToken);

    /// <summary>The process published from <paramref name="project"/>.</summary>
    Task<ProcessRecord?> FindProcessAsync(string project, CancellationToken cancellationToken);

    /// <summary>All processes.</summary>
    Task<IReadOnlyList<ProcessRecord>> ListProcessesAsync(CancellationToken cancellationToken);

    /// <summary>Inserts a process (revision 0); false when the id or project and path exist.</summary>
    Task<bool> InsertProcessAsync(ProcessRecord process, CancellationToken cancellationToken);

    /// <summary>Updates a process if its revision is current.</summary>
    Task<bool> UpdateProcessAsync(ProcessRecord process, CancellationToken cancellationToken);

    /// <summary>Inserts a published package; false when that version exists (versions are never replaced).</summary>
    Task<bool> InsertPackageAsync(PackageRecord package, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>A package's record.</summary>
    Task<PackageRecord?> GetPackageAsync(string processId, int version, CancellationToken cancellationToken);

    /// <summary>A process's packages, newest first.</summary>
    Task<IReadOnlyList<PackageRecord>> ListPackagesAsync(string processId, CancellationToken cancellationToken);

    /// <summary>A package's bytes.</summary>
    Task<byte[]?> ReadPackageContentAsync(string processId, int version, CancellationToken cancellationToken);

    /// <summary>Inserts a job; false when its id or idempotency key exists.</summary>
    Task<bool> InsertJobAsync(JobRecord job, CancellationToken cancellationToken);

    /// <summary>A job.</summary>
    Task<JobRecord?> GetJobAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>The job created with an idempotency key.</summary>
    Task<JobRecord?> FindJobByIdempotencyKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>Updates a job if its revision is current.</summary>
    Task<bool> UpdateJobAsync(JobRecord job, CancellationToken cancellationToken);

    /// <summary>Queued, assigned and running jobs (dispatch and lease checks).</summary>
    Task<IReadOnlyList<JobRecord>> ListActiveJobsAsync(CancellationToken cancellationToken);

    /// <summary>Jobs matching a query, newest first, with the total count.</summary>
    Task<JobPage> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken);

    /// <summary>Counts by state, and completions per time bucket since <paramref name="since"/>.</summary>
    Task<JobStats> GetStatsAsync(DateTimeOffset since, TimeSpan bucket, CancellationToken cancellationToken);

    /// <summary>Stores events a job does not have yet (by attempt and sequence); returns how many were new.</summary>
    Task<int> AppendEventsAsync(string jobId, IReadOnlyList<StoredEvent> events, CancellationToken cancellationToken);

    /// <summary>A job's events in order, after a position (attempt and sequence).</summary>
    Task<IReadOnlyList<StoredEvent>> ReadEventsAsync(string jobId, int afterAttempt, long afterSequence, int limit, CancellationToken cancellationToken);

    /// <summary>A Runner.</summary>
    Task<RunnerRecord?> GetRunnerAsync(string runnerId, CancellationToken cancellationToken);

    /// <summary>All Runners.</summary>
    Task<IReadOnlyList<RunnerRecord>> ListRunnersAsync(CancellationToken cancellationToken);

    /// <summary>Inserts a Runner; false when the id or name exists.</summary>
    Task<bool> InsertRunnerAsync(RunnerRecord runner, CancellationToken cancellationToken);

    /// <summary>Updates a Runner if its revision is current.</summary>
    Task<bool> UpdateRunnerAsync(RunnerRecord runner, CancellationToken cancellationToken);

    /// <summary>A schedule.</summary>
    Task<ScheduleRecord?> GetScheduleAsync(string scheduleId, CancellationToken cancellationToken);

    /// <summary>All schedules.</summary>
    Task<IReadOnlyList<ScheduleRecord>> ListSchedulesAsync(CancellationToken cancellationToken);

    /// <summary>Inserts a schedule.</summary>
    Task<bool> InsertScheduleAsync(ScheduleRecord schedule, CancellationToken cancellationToken);

    /// <summary>Updates a schedule if its revision is current.</summary>
    Task<bool> UpdateScheduleAsync(ScheduleRecord schedule, CancellationToken cancellationToken);

    /// <summary>Deletes a schedule.</summary>
    Task<bool> DeleteScheduleAsync(string scheduleId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a scheduled job and records the fire on its schedule in one transaction, so a fire time creates its job
    /// exactly once even across restarts. False (nothing written) when the schedule's revision is stale or the job's
    /// idempotency key exists.
    /// </summary>
    Task<bool> FireScheduleAsync(ScheduleRecord schedule, JobRecord? job, CancellationToken cancellationToken);

    /// <summary>An access token.</summary>
    Task<AccessTokenRecord?> GetAccessTokenAsync(string tokenId, CancellationToken cancellationToken);

    /// <summary>All access tokens.</summary>
    Task<IReadOnlyList<AccessTokenRecord>> ListAccessTokensAsync(CancellationToken cancellationToken);

    /// <summary>Inserts an access token.</summary>
    Task<bool> InsertAccessTokenAsync(AccessTokenRecord token, CancellationToken cancellationToken);

    /// <summary>Updates an access token if its revision is current.</summary>
    Task<bool> UpdateAccessTokenAsync(AccessTokenRecord token, CancellationToken cancellationToken);

    /// <summary>Appends to the audit history.</summary>
    Task AppendAuditAsync(DateTimeOffset time, string actor, string action, string? target, string? details, CancellationToken cancellationToken);

    /// <summary>Audit entries, newest first.</summary>
    Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int offset, int limit, CancellationToken cancellationToken);

    /// <summary>The execution policy.</summary>
    Task<OrchestrationPolicy> GetPolicyAsync(CancellationToken cancellationToken);

    /// <summary>Saves the execution policy.</summary>
    Task SavePolicyAsync(OrchestrationPolicy policy, CancellationToken cancellationToken);

    /// <summary>Storage facts for the administration page.</summary>
    Task<StoreDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken);
}

/// <summary>A job list query.</summary>
public sealed record JobQuery
{
    /// <summary>Only these states (empty: all).</summary>
    public IReadOnlyList<JobState> States { get; init; } = [];

    /// <summary>Only this process.</summary>
    public string? ProcessId { get; init; }

    /// <summary>Only this Runner.</summary>
    public string? RunnerId { get; init; }

    /// <summary>Only this schedule.</summary>
    public string? ScheduleId { get; init; }

    /// <summary>Created at or after.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Skip.</summary>
    public int Offset { get; init; }

    /// <summary>Take (1–200).</summary>
    public int Limit { get; init; } = 50;
}

/// <summary>A page of jobs.</summary>
/// <param name="Jobs">The jobs, newest first.</param>
/// <param name="Total">How many match the query.</param>
public sealed record JobPage(IReadOnlyList<JobRecord> Jobs, int Total);

/// <summary>Job counts.</summary>
/// <param name="ByState">Count per state (all jobs).</param>
/// <param name="Buckets">Final outcomes per time bucket.</param>
public sealed record JobStats(IReadOnlyDictionary<JobState, int> ByState, IReadOnlyList<JobStatsBucket> Buckets);

/// <summary>Final outcomes in one time bucket.</summary>
/// <param name="Start">Bucket start.</param>
/// <param name="Succeeded">Succeeded jobs.</param>
/// <param name="Failed">Failed and timed-out jobs.</param>
/// <param name="Cancelled">Cancelled jobs.</param>
/// <param name="Interrupted">Interrupted jobs.</param>
public sealed record JobStatsBucket(DateTimeOffset Start, int Succeeded, int Failed, int Cancelled, int Interrupted);

/// <summary>Storage facts.</summary>
/// <param name="Provider">Store kind.</param>
/// <param name="Location">Where it is.</param>
/// <param name="SizeBytes">Size on disk.</param>
/// <param name="SchemaVersion">Storage schema version.</param>
/// <param name="Counts">Rows per table.</param>
public sealed record StoreDiagnostics(string Provider, string Location, long SizeBytes, int SchemaVersion, IReadOnlyDictionary<string, long> Counts);

/// <summary>Source-generated JSON of the stored records: camelCase, enums as names, nulls omitted.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(JobRecord))]
[JsonSerializable(typeof(ProcessRecord))]
[JsonSerializable(typeof(PackageRecord))]
[JsonSerializable(typeof(PackageManifest))]
[JsonSerializable(typeof(RunnerRecord))]
[JsonSerializable(typeof(ScheduleRecord))]
[JsonSerializable(typeof(AccessTokenRecord))]
[JsonSerializable(typeof(OrchestrationPolicy))]
[JsonSerializable(typeof(PackageDocument))]
public sealed partial class OrchestrationJson : JsonSerializerContext;
