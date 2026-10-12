namespace MyRPA.Orchestration;

/// <summary>
/// Where a job is in its life (ADR-0049). <see cref="Succeeded"/>, <see cref="Failed"/>, <see cref="Cancelled"/> and
/// <see cref="TimedOut"/> are final. <see cref="Interrupted"/> means the outcome is unknown (the Runner went silent while
/// running): it is never retried automatically, and only a late completion under the same lease can settle it.
/// </summary>
public enum JobState
{
    /// <summary>Accepted and waiting for an eligible Runner.</summary>
    Queued = 0,

    /// <summary>Claimed by a Runner under a lease; not started yet.</summary>
    Assigned = 1,

    /// <summary>The Runner started the workflow.</summary>
    Running = 2,

    /// <summary>The workflow succeeded.</summary>
    Succeeded = 3,

    /// <summary>The workflow (or its preparation) failed.</summary>
    Failed = 4,

    /// <summary>Cancelled on request.</summary>
    Cancelled = 5,

    /// <summary>The workflow exceeded its time limit.</summary>
    TimedOut = 6,

    /// <summary>The Runner was lost while the workflow ran: the outcome is unknown.</summary>
    Interrupted = 7,
}

/// <summary>Access roles (ADR-0051), ordered: each includes the rights of the ones before it.</summary>
public enum Role
{
    /// <summary>Reads jobs, Runners, deployments and history.</summary>
    Viewer = 0,

    /// <summary>Also submits, cancels and retries jobs, and enables or disables schedules.</summary>
    Operator = 1,

    /// <summary>Also publishes, deploys, rolls back and manages schedules.</summary>
    Developer = 2,

    /// <summary>Also manages Runners, access tokens and policies, and reads the audit history.</summary>
    Admin = 3,
}

/// <summary>A Runner's status as computed from its record and the clock.</summary>
public enum RunnerStatus
{
    /// <summary>No heartbeat within the offline threshold.</summary>
    Offline = 0,

    /// <summary>Online with free capacity.</summary>
    Available = 1,

    /// <summary>Online and at capacity.</summary>
    Busy = 2,

    /// <summary>Online, but the administrator paused dispatch to it.</summary>
    Paused = 3,

    /// <summary>Online, but some of its plugins failed to load.</summary>
    Error = 4,

    /// <summary>Revoked: its token no longer works.</summary>
    Disabled = 5,
}

/// <summary>How a schedule fires.</summary>
public enum ScheduleKind
{
    /// <summary>Once, at <see cref="ScheduleRecord.StartAt"/>.</summary>
    Once = 0,

    /// <summary>Every <see cref="ScheduleRecord.IntervalMinutes"/> from <see cref="ScheduleRecord.StartAt"/>.</summary>
    Interval = 1,

    /// <summary>At the times of a five-field cron expression in the schedule's time zone.</summary>
    Cron = 2,
}

/// <summary>What happens to fire times that passed while the Server was not evaluating (it was stopped).</summary>
public enum MisfirePolicy
{
    /// <summary>One job for the missed times, then continue.</summary>
    RunOnce = 0,

    /// <summary>Skip the missed times.</summary>
    Skip = 1,
}

/// <summary>What happens when a schedule fires while a job it created is still active.</summary>
public enum OverlapPolicy
{
    /// <summary>Skip this fire time.</summary>
    Skip = 0,

    /// <summary>Create the job anyway.</summary>
    Allow = 1,
}

/// <summary>A failure, as stored with a job (never contains secrets or workflow data).</summary>
/// <param name="Code">Error code (for example <c>MYRPA2001</c> or <c>MYRPA5001</c>).</param>
/// <param name="Message">What went wrong.</param>
/// <param name="ErrorType">Classification (for example <c>DependencyMissing</c>, <c>Interrupted</c>).</param>
/// <param name="NodeId">The node where it originated.</param>
/// <param name="ActivityType">That node's activity type.</param>
public sealed record JobError(string Code, string Message, string? ErrorType = null, string? NodeId = null, string? ActivityType = null);

/// <summary>One dispatch of a job to a Runner.</summary>
public sealed record JobAttempt
{
    /// <summary>1, 2, …</summary>
    public required int Number { get; init; }

    /// <summary>The Runner.</summary>
    public required string RunnerId { get; init; }

    /// <summary>The lease of this attempt.</summary>
    public required string LeaseId { get; init; }

    /// <summary>When the Runner claimed the job.</summary>
    public required DateTimeOffset AssignedAt { get; init; }

    /// <summary>When the Runner started the workflow.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the attempt ended.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>How it ended (a <see cref="JobState"/> name, or <c>Requeued</c>).</summary>
    public string? Outcome { get; init; }

    /// <summary>Why it ended that way.</summary>
    public string? Reason { get; init; }
}

/// <summary>A job: one execution request of an exact published version.</summary>
public sealed record JobRecord
{
    /// <summary>The job id.</summary>
    public required string JobId { get; init; }

    /// <summary>The process (deployed workflow).</summary>
    public required string ProcessId { get; init; }

    /// <summary>The process name at submission.</summary>
    public required string ProcessName { get; init; }

    /// <summary>The exact version (never switched to a later one).</summary>
    public required int Version { get; init; }

    /// <summary>The package hash of that version.</summary>
    public required string PackageSha256 { get; init; }

    /// <summary>The entry point (workflow path inside the package) it starts.</summary>
    public required string EntryPath { get; init; }

    /// <summary>Validated input arguments (JSON object).</summary>
    public string ArgumentsJson { get; init; } = "{}";

    /// <summary>Run time limit.</summary>
    public int? TimeoutMs { get; init; }

    /// <summary>Higher runs first.</summary>
    public int Priority { get; init; }

    /// <summary>Only this Runner may run it.</summary>
    public string? TargetRunnerId { get; init; }

    /// <summary>Only Runners of this group may run it.</summary>
    public string? TargetGroup { get; init; }

    /// <summary>Activity types the package needs.</summary>
    public IReadOnlyList<string> RequiredActivities { get; init; } = [];

    /// <summary>Plugins (id and minimum version) the package needs.</summary>
    public IReadOnlyList<PackagePlugin> RequiredPlugins { get; init; } = [];

    /// <summary>Current state.</summary>
    public JobState State { get; init; }

    /// <summary>Accepted (persisted) at.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last assignment.</summary>
    public DateTimeOffset? AssignedAt { get; init; }

    /// <summary>Workflow start.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Final state reached.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>The Runner of the current or last attempt.</summary>
    public string? RunnerId { get; init; }

    /// <summary>The current lease (null when not assigned).</summary>
    public string? LeaseId { get; init; }

    /// <summary>When the current lease expires unless renewed.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; init; }

    /// <summary>Dispatch attempts so far.</summary>
    public int AttemptCount { get; init; }

    /// <summary>Most dispatch attempts for not-started failures (lease expiry before start).</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Attempts, oldest first.</summary>
    public IReadOnlyList<JobAttempt> Attempts { get; init; } = [];

    /// <summary>Correlation id shared by Server and Runner logs.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Cancellation was requested.</summary>
    public bool CancelRequested { get; init; }

    /// <summary>Who requested cancellation.</summary>
    public string? CancelRequestedBy { get; init; }

    /// <summary><c>manual</c>, <c>schedule</c> or <c>retry</c>.</summary>
    public string Source { get; init; } = "manual";

    /// <summary>The schedule that created it.</summary>
    public string? ScheduleId { get; init; }

    /// <summary>The schedule fire time it stands for.</summary>
    public DateTimeOffset? ScheduledFor { get; init; }

    /// <summary>The job this one retries.</summary>
    public string? RetryOfJobId { get; init; }

    /// <summary>Client idempotency key (unique).</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Who submitted it.</summary>
    public required string SubmittedBy { get; init; }

    /// <summary>The failure.</summary>
    public JobError? Error { get; init; }

    /// <summary>Output arguments (JSON object; secret-fed outputs redacted).</summary>
    public string? OutputsJson { get; init; }

    /// <summary>Run duration reported by the Runner.</summary>
    public double? DurationMs { get; init; }

    /// <summary>The engine's execution id.</summary>
    public string? ExecutionId { get; init; }

    /// <summary>Events stored for the job.</summary>
    public int EventCount { get; init; }

    /// <summary>Events refused because the per-job limit was reached.</summary>
    public int EventsDropped { get; init; }

    /// <summary>Optimistic concurrency revision (the store increments it on every update).</summary>
    public long Revision { get; init; }

    /// <summary>Whether <see cref="State"/> is final.</summary>
    public bool IsFinal => JobTransitions.IsFinal(State);

    /// <summary>Whether the job is queued, assigned or running.</summary>
    public bool IsActive => State is JobState.Queued or JobState.Assigned or JobState.Running;
}

/// <summary>A plugin a package needs: its id and the version it was published with (the minimum).</summary>
/// <param name="Id">Plugin id.</param>
/// <param name="Version">Version at publication.</param>
public sealed record PackagePlugin(string Id, string Version);

/// <summary>
/// A process: a workflow of a project that is published as immutable versions and deployed (ADR-0049). Publishing adds
/// a version; deploying chooses which version jobs and schedules use; rolling back deploys an earlier version.
/// </summary>
public sealed record ProcessRecord
{
    /// <summary>The process id.</summary>
    public required string ProcessId { get; init; }

    /// <summary>Display name (the project's name).</summary>
    public required string Name { get; init; }

    /// <summary>The project it is published from (one process per project).</summary>
    public required string Project { get; init; }

    /// <summary>First publication.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The latest published version.</summary>
    public int LatestVersion { get; init; }

    /// <summary>The deployed version (null: published, not deployed).</summary>
    public int? DeployedVersion { get; init; }

    /// <summary>When the deployment last changed.</summary>
    public DateTimeOffset? DeployedAt { get; init; }

    /// <summary>Who changed it.</summary>
    public string? DeployedBy { get; init; }

    /// <summary>Whether the deployment accepts jobs (manual and scheduled).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Runner group jobs go to (null: any).</summary>
    public string? TargetGroup { get; init; }

    /// <summary>Runner jobs go to (null: any eligible).</summary>
    public string? TargetRunnerId { get; init; }

    /// <summary>Default job priority.</summary>
    public int Priority { get; init; }

    /// <summary>Default job time limit.</summary>
    public int? TimeoutMs { get; init; }

    /// <summary>Optimistic concurrency revision.</summary>
    public long Revision { get; init; }
}

/// <summary>A published, immutable version of a process.</summary>
public sealed record PackageRecord
{
    /// <summary>The process.</summary>
    public required string ProcessId { get; init; }

    /// <summary>The version (1, 2, …).</summary>
    public required int Version { get; init; }

    /// <summary>SHA-256 (hex) of the package bytes.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Package size in bytes.</summary>
    public required long Size { get; init; }

    /// <summary>The manifest.</summary>
    public required PackageManifest Manifest { get; init; }
}

/// <summary>What a package holds and needs (ADR-0049).</summary>
public sealed record PackageManifest
{
    /// <summary>Package format.</summary>
    public string Format { get; init; } = WorkflowPackage.FormatName;

    /// <summary>The process.</summary>
    public required string ProcessId { get; init; }

    /// <summary>The process name.</summary>
    public required string Name { get; init; }

    /// <summary>The version.</summary>
    public required int Version { get; init; }

    /// <summary>The project it was published from.</summary>
    public required string Project { get; init; }

    /// <summary>The workflows a job may start (at least one).</summary>
    public required IReadOnlyList<PackageEntryPoint> EntryPoints { get; init; }

    /// <summary>Workflow files with their hashes.</summary>
    public IReadOnlyList<PackageFile> Files { get; init; } = [];

    /// <summary>Activity types used by the packaged workflows.</summary>
    public IReadOnlyList<string> Activities { get; init; } = [];

    /// <summary>Plugins providing non-built-in activities, with the versions published against.</summary>
    public IReadOnlyList<PackagePlugin> Plugins { get; init; } = [];

    /// <summary>Project files left out (not valid workflows), with the reason.</summary>
    public IReadOnlyList<string> Excluded { get; init; } = [];

    /// <summary>Publication time.</summary>
    public required DateTimeOffset PublishedAt { get; init; }

    /// <summary>Who published.</summary>
    public required string PublishedBy { get; init; }

    /// <summary>Release notes.</summary>
    public string? Notes { get; init; }
}

/// <summary>A file in a package.</summary>
/// <param name="Path">Package-relative path with <c>/</c>.</param>
/// <param name="Sha256">SHA-256 (hex) of its UTF-8 content.</param>
/// <param name="Size">Size in bytes.</param>
public sealed record PackageFile(string Path, string Sha256, long Size);

/// <summary>An argument of an entry-point workflow, for run dialogs.</summary>
/// <param name="Name">Argument name.</param>
/// <param name="Direction">In, Out or InOut.</param>
/// <param name="Type">Data type.</param>
/// <param name="Required">Whether it must be given.</param>
/// <param name="Secret">Whether it feeds a secret property (then a job may not set it).</param>
public sealed record PackageArgument(string Name, string Direction, string Type, bool Required, bool Secret);

/// <summary>A workflow of a package that a job may start.</summary>
public sealed record PackageEntryPoint
{
    /// <summary>The workflow's path in the package.</summary>
    public required string Path { get; init; }

    /// <summary>The workflow's id.</summary>
    public required string WorkflowId { get; init; }

    /// <summary>The workflow's name.</summary>
    public required string Name { get; init; }

    /// <summary>The workflow's version field.</summary>
    public string? WorkflowVersion { get; init; }

    /// <summary>The workflow's schema version.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>The workflow's arguments.</summary>
    public IReadOnlyList<PackageArgument> Arguments { get; init; } = [];
}

/// <summary>A Runner as the administrator created it and as it last registered.</summary>
public sealed record RunnerRecord
{
    /// <summary>The Runner id.</summary>
    public required string RunnerId { get; init; }

    /// <summary>Name.</summary>
    public required string Name { get; init; }

    /// <summary>Group (dispatch target), set by the administrator.</summary>
    public string? Group { get; init; }

    /// <summary>SHA-256 (hex) of the token secret.</summary>
    public required string TokenHash { get; init; }

    /// <summary>Created at.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Revoked: the token no longer authenticates.</summary>
    public bool Disabled { get; init; }

    /// <summary>Paused: no new jobs.</summary>
    public bool Paused { get; init; }

    /// <summary>Administrator cap on concurrent jobs.</summary>
    public int MaxConcurrency { get; init; } = 1;

    /// <summary>Last registration (null: never connected).</summary>
    public DateTimeOffset? RegisteredAt { get; init; }

    /// <summary>Last request.</summary>
    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>Runner software version.</summary>
    public string? Version { get; init; }

    /// <summary>Operating system.</summary>
    public string? OperatingSystem { get; init; }

    /// <summary>Architecture.</summary>
    public string? Architecture { get; init; }

    /// <summary>Host machine.</summary>
    public string? MachineName { get; init; }

    /// <summary>Runs as a service (no interactive desktop).</summary>
    public bool Service { get; init; }

    /// <summary>Concurrency the Runner reported.</summary>
    public int ReportedConcurrency { get; init; }

    /// <summary>Plugins it reported.</summary>
    public IReadOnlyList<PackagePlugin> Plugins { get; init; } = [];

    /// <summary>Activity types it reported.</summary>
    public IReadOnlyList<string> Activities { get; init; } = [];

    /// <summary>Plugin load failures it reported.</summary>
    public IReadOnlyList<string> PluginErrors { get; init; } = [];

    /// <summary>Optimistic concurrency revision.</summary>
    public long Revision { get; init; }

    /// <summary>The cap that applies: the lower of the administrator's and the Runner's.</summary>
    public int Capacity => ReportedConcurrency > 0 ? Math.Min(MaxConcurrency, ReportedConcurrency) : MaxConcurrency;
}

/// <summary>A schedule that creates jobs for a process.</summary>
public sealed record ScheduleRecord
{
    /// <summary>The schedule id.</summary>
    public required string ScheduleId { get; init; }

    /// <summary>The process.</summary>
    public required string ProcessId { get; init; }

    /// <summary>The entry point it starts (a path in the deployed package).</summary>
    public required string EntryPath { get; init; }

    /// <summary>Name.</summary>
    public required string Name { get; init; }

    /// <summary>Kind.</summary>
    public required ScheduleKind Kind { get; init; }

    /// <summary>Five-field cron expression (<see cref="ScheduleKind.Cron"/>).</summary>
    public string? Cron { get; init; }

    /// <summary>Interval (<see cref="ScheduleKind.Interval"/>).</summary>
    public int? IntervalMinutes { get; init; }

    /// <summary>First (or only) fire time; for cron, schedules fire from then on.</summary>
    public DateTimeOffset? StartAt { get; init; }

    /// <summary>IANA or Windows time zone id the cron expression is read in.</summary>
    public required string TimeZone { get; init; }

    /// <summary>Whether it fires.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Missed fire times.</summary>
    public MisfirePolicy Misfire { get; init; }

    /// <summary>Overlap with its own active jobs.</summary>
    public OverlapPolicy Overlap { get; init; }

    /// <summary>Arguments for created jobs (JSON object).</summary>
    public string ArgumentsJson { get; init; } = "{}";

    /// <summary>Created at.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Created by.</summary>
    public required string CreatedBy { get; init; }

    /// <summary>Last fire time handled.</summary>
    public DateTimeOffset? LastFireAt { get; init; }

    /// <summary>Next fire time (null: no more).</summary>
    public DateTimeOffset? NextFireAt { get; init; }

    /// <summary>What happened at the last fire time.</summary>
    public string? LastOutcome { get; init; }

    /// <summary>Optimistic concurrency revision.</summary>
    public long Revision { get; init; }
}

/// <summary>An access token for the management API (ADR-0051).</summary>
public sealed record AccessTokenRecord
{
    /// <summary>The token id (also part of the token text).</summary>
    public required string TokenId { get; init; }

    /// <summary>What it is for.</summary>
    public required string Name { get; init; }

    /// <summary>Its role.</summary>
    public required Role Role { get; init; }

    /// <summary>SHA-256 (hex) of the secret.</summary>
    public required string Hash { get; init; }

    /// <summary>Created at.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Created by.</summary>
    public required string CreatedBy { get; init; }

    /// <summary>Revoked at (then it no longer authenticates).</summary>
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>Optimistic concurrency revision.</summary>
    public long Revision { get; init; }
}

/// <summary>An administrative action, as recorded in the audit history.</summary>
/// <param name="Id">Sequence number.</param>
/// <param name="Time">When.</param>
/// <param name="Actor">Who (the local owner, or an access token's name).</param>
/// <param name="Action">What (for example <c>runner.create</c>).</param>
/// <param name="Target">On what.</param>
/// <param name="Details">Details (never secrets).</param>
public sealed record AuditEntry(long Id, DateTimeOffset Time, string Actor, string Action, string? Target, string? Details);

/// <summary>A stored execution event of a job (the Runner's <c>ExecutionEventMessage</c>, as JSON).</summary>
/// <param name="Attempt">The attempt it belongs to.</param>
/// <param name="Sequence">Its sequence in that attempt's run.</param>
/// <param name="Kind">Event kind.</param>
/// <param name="Time">When it happened.</param>
/// <param name="Json">The message.</param>
public sealed record StoredEvent(int Attempt, long Sequence, string Kind, DateTimeOffset Time, string Json);

/// <summary>Server-wide execution policy, set by administrators.</summary>
public sealed record OrchestrationPolicy
{
    /// <summary>Most dispatch attempts of a job whose Runner never started it.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Events stored per job; later ones are counted as dropped.</summary>
    public int MaxEventsPerJob { get; init; } = 20_000;

    /// <summary>A Runner without a request for this long is offline; its leases expire.</summary>
    public int OfflineAfterSeconds { get; init; } = 45;

    /// <summary>Default time limit of jobs without their own (null: the engine default).</summary>
    public int? DefaultTimeoutMinutes { get; init; }
}

/// <summary>Who is acting: a person through the Studio, an access token or the scheduler.</summary>
/// <param name="Name">For audit and job records.</param>
/// <param name="Role">Rights.</param>
public sealed record Actor(string Name, Role Role)
{
    /// <summary>The local owner signed in through the start link (ADR-0025): an administrator.</summary>
    public static Actor LocalOwner { get; } = new("local owner", Role.Admin);

    /// <summary>The scheduler (creates jobs only).</summary>
    public static Actor Scheduler { get; } = new("scheduler", Role.Operator);

    /// <summary>Whether this actor has at least <paramref name="role"/>.</summary>
    public bool Has(Role role) => Role >= role;
}
