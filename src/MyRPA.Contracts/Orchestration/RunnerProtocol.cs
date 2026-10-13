using System.Text.Json.Serialization;
using MyRPA.Contracts.Execution;

namespace MyRPA.Contracts.Orchestration;

/// <summary>
/// The Runner protocol (ADR-0049): a Runner calls the Server over HTTPS (it never accepts connections), authenticated
/// by its own token. Every request carries the protocol version; the Server refuses versions it does not speak.
/// </summary>
public static class RunnerProtocol
{
    /// <summary>The protocol version this assembly speaks.</summary>
    public const int Version = 1;

    /// <summary>Path prefix of the Runner endpoints on the Runner listener.</summary>
    public const string PathPrefix = "/runner/v1";

    /// <summary>Header naming the protocol version of a request.</summary>
    public const string VersionHeader = "X-MyRPA-Runner-Protocol";

    /// <summary>Header carrying a request correlation id (echoed in Server logs, never secret).</summary>
    public const string CorrelationHeader = "X-MyRPA-Correlation";

    /// <summary>Longest a claim request may wait for a job.</summary>
    public const int MaxClaimWaitMs = 25_000;

    /// <summary>Most events one events request may carry.</summary>
    public const int MaxEventsPerRequest = 500;
}

/// <summary>A plugin installed on a Runner.</summary>
/// <param name="Id">Plugin id from its manifest.</param>
/// <param name="Version">Plugin version from its manifest.</param>
public sealed record RunnerPluginInfo(string Id, string Version);

/// <summary>A job a Runner holds, with the lease it holds it under.</summary>
/// <param name="JobId">The job.</param>
/// <param name="LeaseId">The lease from the assignment.</param>
public sealed record RunnerActiveJob(string JobId, string LeaseId);

/// <summary><c>POST /runner/v1/register</c>: what this Runner is and can do (from its own trusted configuration).</summary>
public sealed record RunnerRegisterRequest
{
    /// <summary>Protocol version (<see cref="RunnerProtocol.Version"/>).</summary>
    public required int ProtocolVersion { get; init; }

    /// <summary>The Runner software version.</summary>
    public required string RunnerVersion { get; init; }

    /// <summary>Operating system description.</summary>
    public required string OperatingSystem { get; init; }

    /// <summary>Process architecture (X64, Arm64, …).</summary>
    public required string Architecture { get; init; }

    /// <summary>Host machine name.</summary>
    public string? MachineName { get; init; }

    /// <summary>Whether the Runner runs as a service without an interactive desktop (ADR-0049).</summary>
    public bool Service { get; init; }

    /// <summary>Jobs the Runner can run at once (the Server applies its own, lower cap if set).</summary>
    public required int MaxConcurrency { get; init; }

    /// <summary>Loaded plugins.</summary>
    public IReadOnlyList<RunnerPluginInfo> Plugins { get; init; } = [];

    /// <summary>Activity types the Runner's catalog offers.</summary>
    public IReadOnlyList<string> Activities { get; init; } = [];

    /// <summary>Plugin load failures (optional plugins that did not load), as messages.</summary>
    public IReadOnlyList<string> PluginErrors { get; init; } = [];

    /// <summary>Jobs the Runner is still executing (empty after a restart).</summary>
    public IReadOnlyList<RunnerActiveJob> ActiveJobs { get; init; } = [];

    /// <summary>
    /// Package formats the Runner reads, such as <c>myrpa-package/2</c>. Absent (Runners from before this field): the
    /// first format only. The Server never assigns a job whose package the Runner cannot read.
    /// </summary>
    public IReadOnlyList<string>? PackageFormats { get; init; }
}

/// <summary>Answer to a registration.</summary>
public sealed record RunnerRegisterResponse
{
    /// <summary>The Runner's id (from its token).</summary>
    public required string RunnerId { get; init; }

    /// <summary>The Runner's name, as the administrator created it.</summary>
    public required string Name { get; init; }

    /// <summary>How often to send heartbeats.</summary>
    public required int HeartbeatIntervalMs { get; init; }

    /// <summary>The Server's clock.</summary>
    public required DateTimeOffset ServerTime { get; init; }
}

/// <summary><c>POST /runner/v1/heartbeat</c>.</summary>
public sealed record RunnerHeartbeatRequest
{
    /// <summary>Jobs the Runner holds; their leases are renewed.</summary>
    public IReadOnlyList<RunnerActiveJob> ActiveJobs { get; init; } = [];
}

/// <summary>Answer to a heartbeat.</summary>
public sealed record RunnerHeartbeatResponse
{
    /// <summary>Held jobs whose cancellation was requested.</summary>
    public IReadOnlyList<string> Cancel { get; init; } = [];

    /// <summary>Held jobs whose lease is no longer valid; the Runner stops them and reports nothing more for them.</summary>
    public IReadOnlyList<string> Lost { get; init; } = [];

    /// <summary>The administrator paused this Runner: it finishes its jobs but claims none.</summary>
    public bool Paused { get; init; }
}

/// <summary><c>POST /runner/v1/claim</c>: asks for one job, waiting up to <see cref="WaitMs"/> for one.</summary>
public sealed record RunnerClaimRequest
{
    /// <summary>How long to wait for a job (at most <see cref="RunnerProtocol.MaxClaimWaitMs"/>).</summary>
    public int WaitMs { get; init; }
}

/// <summary>A job assigned to a Runner under a lease (the answer to a successful claim).</summary>
public sealed record JobAssignment
{
    /// <summary>The job.</summary>
    public required string JobId { get; init; }

    /// <summary>The lease; every later message about the job carries it.</summary>
    public required string LeaseId { get; init; }

    /// <summary>Dispatch attempt (1 for the first).</summary>
    public required int Attempt { get; init; }

    /// <summary>The process (deployed workflow).</summary>
    public required string ProcessId { get; init; }

    /// <summary>The process name.</summary>
    public required string ProcessName { get; init; }

    /// <summary>The exact published version to run.</summary>
    public required int Version { get; init; }

    /// <summary>SHA-256 (hex) of the package; the Runner refuses a package with another hash.</summary>
    public required string PackageSha256 { get; init; }

    /// <summary>The entry workflow's path inside the package.</summary>
    public required string EntryPath { get; init; }

    /// <summary>Input arguments as a JSON object (validated by the Server against the entry workflow).</summary>
    public required string ArgumentsJson { get; init; }

    /// <summary>Run time limit; null uses the engine default.</summary>
    public int? TimeoutMs { get; init; }

    /// <summary>Correlation id for logs across Server and Runner.</summary>
    public required string CorrelationId { get; init; }
}

/// <summary><c>POST /runner/v1/jobs/{jobId}/start</c>: the Runner is about to execute the job.</summary>
/// <param name="LeaseId">The assignment's lease.</param>
/// <param name="RunId">The Runner's run id (ExecutionHost), for its own logs.</param>
public sealed record JobStartRequest(string LeaseId, string? RunId);

/// <summary>Answer to start, events and complete requests.</summary>
public sealed record JobLeaseResponse
{
    /// <summary>Whether the lease is still valid (false: stop, report nothing more).</summary>
    public required bool LeaseValid { get; init; }

    /// <summary>Cancellation of the job was requested.</summary>
    public bool CancelRequested { get; init; }

    /// <summary>The job's state on the Server.</summary>
    public string? State { get; init; }

    /// <summary>The message repeated one the Server already had (idempotent replay).</summary>
    public bool Duplicate { get; init; }
}

/// <summary><c>POST /runner/v1/jobs/{jobId}/events</c>: a batch of the run's events (at least once; deduplicated by sequence).</summary>
public sealed record JobEventsRequest
{
    /// <summary>The assignment's lease.</summary>
    public required string LeaseId { get; init; }

    /// <summary>Events in sequence order (at most <see cref="RunnerProtocol.MaxEventsPerRequest"/>).</summary>
    public IReadOnlyList<ExecutionEventMessage> Events { get; init; } = [];
}

/// <summary><c>POST /runner/v1/jobs/{jobId}/complete</c>: the outcome of the job.</summary>
public sealed record JobCompleteRequest
{
    /// <summary>The assignment's lease.</summary>
    public required string LeaseId { get; init; }

    /// <summary>Succeeded, Failed, Cancelled or TimedOut.</summary>
    public required string Status { get; init; }

    /// <summary>The failure, when not succeeded.</summary>
    public ExecutionErrorMessage? Error { get; init; }

    /// <summary>Output arguments as a JSON object (secret-fed outputs are redacted by the Runner).</summary>
    public string? OutputsJson { get; init; }

    /// <summary>Run duration.</summary>
    public double? DurationMs { get; init; }

    /// <summary>The engine's execution id.</summary>
    public string? ExecutionId { get; init; }

    /// <summary>
    /// The workflow never started (package or dependency check failed, or the Runner refused it): no side effect can
    /// have happened.
    /// </summary>
    public bool NotStarted { get; init; }
}

/// <summary><c>POST /runner/v1/jobs/{jobId}/release</c>: gives back a job that was not started (shutdown).</summary>
/// <param name="LeaseId">The assignment's lease.</param>
/// <param name="Reason">Why.</param>
public sealed record JobReleaseRequest(string LeaseId, string? Reason);

/// <summary>An error answer of the Runner endpoints.</summary>
/// <param name="Error">What went wrong (never contains secrets).</param>
public sealed record RunnerErrorResponse(string Error);

/// <summary>Source-generated JSON for the Runner protocol: camelCase names, null members omitted.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RunnerRegisterRequest))]
[JsonSerializable(typeof(RunnerRegisterResponse))]
[JsonSerializable(typeof(RunnerHeartbeatRequest))]
[JsonSerializable(typeof(RunnerHeartbeatResponse))]
[JsonSerializable(typeof(RunnerClaimRequest))]
[JsonSerializable(typeof(JobAssignment))]
[JsonSerializable(typeof(JobStartRequest))]
[JsonSerializable(typeof(JobLeaseResponse))]
[JsonSerializable(typeof(JobEventsRequest))]
[JsonSerializable(typeof(JobCompleteRequest))]
[JsonSerializable(typeof(JobReleaseRequest))]
[JsonSerializable(typeof(RunnerErrorResponse))]
public sealed partial class RunnerProtocolJsonContext : JsonSerializerContext;
