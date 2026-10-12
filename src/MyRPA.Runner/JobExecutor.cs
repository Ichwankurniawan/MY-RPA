using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyRPA.Contracts.Execution;
using MyRPA.Contracts.Orchestration;
using MyRPA.Core.Activities;
using MyRPA.Core.Execution;
using MyRPA.Execution.Hosting;
using MyRPA.Orchestration;
using MyRPA.Plugins;
using MyRPA.Workflow;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Validation;
using MyRPA.Workflow.Values;

namespace MyRPA.Runner;

/// <summary>
/// Executes assigned jobs (ADR-0049) with the existing engine through <see cref="ExecutionHost"/>: download the exact
/// package, verify its hash and files, check the plugins and activities it needs, unpack it into the job's own work
/// folder, start the run, stream its events, report the outcome and delete the folder. A job whose package or
/// dependencies fail is reported as not started, so it can never have had side effects.
/// </summary>
public sealed partial class JobExecutor
{
    /// <summary>Value written instead of an output fed by a secret.</summary>
    public const string Redacted = "***redacted***";

    private static readonly TimeSpan _flushInterval = TimeSpan.FromMilliseconds(250);

    private readonly RunnerOptions _options;
    private readonly RunnerConnection _connection;
    private readonly ExecutionHost _host;
    private readonly WorkflowLoader _loader;
    private readonly IActivityCatalog _catalog;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly ILogger<JobExecutor> _logger;
    private readonly ConcurrentDictionary<string, ActiveJob> _active = new(StringComparer.Ordinal);

    /// <summary>Creates the executor.</summary>
    public JobExecutor(RunnerOptions options, RunnerConnection connection, ExecutionHost host, WorkflowLoader loader, IActivityCatalog catalog, IServiceProvider services, TimeProvider time, ILogger<JobExecutor> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Jobs held now.</summary>
    public int ActiveCount => _active.Count;

    /// <summary>The jobs held now, with their leases (for heartbeats and registration).</summary>
    public IReadOnlyList<RunnerActiveJob> Held => [.. _active.Values.Select(j => new RunnerActiveJob(j.Assignment.JobId, j.Assignment.LeaseId))];

    /// <summary>Starts executing an assignment in the background.</summary>
    /// <param name="assignment">The job.</param>
    /// <param name="stopping">Signals that the Runner shuts down (a job not yet started is then given back).</param>
    public void Start(JobAssignment assignment, CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        // The job id names the work folder: only the Server's plain ids (lowercase letters, digits, '_') are accepted.
        if (assignment.JobId.Length is 0 or > 100 || !assignment.JobId.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            LogNotStarted(_logger, "(invalid id)", "the Server sent a job id that is not a plain id");
            return;
        }

        var job = new ActiveJob(assignment);
        if (!_active.TryAdd(assignment.JobId, job))
        {
            return;
        }

        job.Task = Task.Run(() => RunAsync(job, stopping), CancellationToken.None);
    }

    /// <summary>The Server asked to cancel a held job (cooperative: the engine stops at the next activity boundary).</summary>
    public void Cancel(string jobId)
    {
        if (_active.TryGetValue(jobId, out var job))
        {
            job.CancelRequested = true;
            if (job.RunId is { } runId)
            {
                _host.Cancel(runId);
            }
        }
    }

    /// <summary>The Server no longer recognizes the job's lease: stop it and report nothing more for it.</summary>
    public void Abandon(string jobId)
    {
        if (_active.TryGetValue(jobId, out var job))
        {
            job.Abandoned = true;
            Cancel(jobId);
        }
    }

    /// <summary>Stops every held job and waits (up to <paramref name="grace"/>) for them to report.</summary>
    public async Task ShutdownAsync(TimeSpan grace)
    {
        foreach (var job in _active.Values)
        {
            job.ShuttingDown = true;
            if (job.RunId is { } runId)
            {
                _host.Cancel(runId);
            }
        }

        var tasks = _active.Values.Select(j => j.Task).OfType<Task>().ToArray();
        try
        {
            await Task.WhenAll(tasks).WaitAsync(grace, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogShutdownTimeout(_logger, tasks.Count(t => !t.IsCompleted));
        }
    }

    /// <summary>Waits for every held job to finish (tests).</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_active.Values.Select(j => j.Task).OfType<Task>());

    /// <summary>The activities and plugins the package needs that this Runner does not have (empty: all present).</summary>
    public IReadOnlyList<string> MissingDependencies(PackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var missing = new List<string>();
        var types = _catalog.Descriptors.Select(d => d.TypeName.Value).ToHashSet(StringComparer.Ordinal);
        missing.AddRange(manifest.Activities.Where(a => !types.Contains(a)).Select(a => $"activity {a}"));
        var plugins = _services.GetService<IPluginRegistry>()?.Plugins ?? [];
        foreach (var needed in manifest.Plugins)
        {
            var installed = plugins.FirstOrDefault(p => p.Manifest.Id.Value == needed.Id);
            if (installed is null)
            {
                missing.Add($"plugin {needed.Id}");
            }
            else if (!WorkflowPackage.Satisfies(installed.Manifest.Version.ToString(), needed.Version))
            {
                missing.Add($"plugin {needed.Id} {needed.Version} or later (installed {installed.Manifest.Version})");
            }
        }

        return missing;
    }

    /// <summary>Output arguments as JSON; outputs fed by a secret property are redacted (ADR-0043).</summary>
    public static string Outputs(WorkflowDefinition workflow, IReadOnlyDictionary<string, object?> outputs)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(outputs);
        var safe = outputs.Select(o => new KeyValuePair<string, object?>(o.Key, workflow.SecretNames.Contains(o.Key) ? Redacted : o.Value));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WorkflowValues.WriteJson(writer, WorkflowValues.Dictionary(safe));
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task RunAsync(ActiveJob job, CancellationToken stopping)
    {
        var assignment = job.Assignment;
        var folder = Path.Combine(_options.WorkDirectory, "jobs", assignment.JobId);
        try
        {
            LogJobReceived(_logger, assignment.JobId, assignment.ProcessName, assignment.Version, assignment.CorrelationId);
            var prepared = await PrepareAsync(job, folder, stopping).ConfigureAwait(false);
            if (prepared is null)
            {
                return;
            }

            if (stopping.IsCancellationRequested)
            {
                await _connection.ReleaseAsync(assignment.JobId, new JobReleaseRequest(assignment.LeaseId, "The Runner is shutting down."), CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var start = await _connection.StartAsync(assignment.JobId, new JobStartRequest(assignment.LeaseId, null), CancellationToken.None).ConfigureAwait(false);
            if (!start.LeaseValid)
            {
                // Cancelled before start, or the lease is gone: nothing ran, nothing to report.
                LogNotStarted(_logger, assignment.JobId, start.State ?? "lease lost");
                return;
            }

            var (workflow, arguments, entry) = prepared.Value;
            var handle = _host.Start(workflow, new ExecutionStartRequest
            {
                Arguments = arguments,
                Timeout = assignment.TimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
                Location = entry,
            });
            job.RunId = handle.RunId;
            if (job.CancelRequested || start.CancelRequested || job.ShuttingDown)
            {
                _host.Cancel(handle.RunId);
            }

            var pump = PumpEventsAsync(job, handle);
            WorkflowExecutionResult? result = null;
            string? hostFailure = null;
            try
            {
                result = await handle.Completion.ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Host failure: reported as a failed job, never swallowed silently.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                hostFailure = ex.Message;
            }

            await pump.ConfigureAwait(false);
            if (job.Abandoned)
            {
                return;
            }

            await ReportAsync(job, workflow, result, hostFailure).ConfigureAwait(false);
        }
        catch (RunnerAuthenticationException)
        {
            LogAuthLost(_logger, assignment.JobId);
        }
        catch (RunnerProtocolException ex)
        {
            // The Server stayed unreachable: the lease will expire there (requeued if not started, interrupted otherwise).
            LogReportFailed(_logger, assignment.JobId, ex.Message);
        }
        finally
        {
            TryDelete(folder);
            _active.TryRemove(assignment.JobId, out _);
        }
    }

    private async Task<(WorkflowDefinition Workflow, IReadOnlyDictionary<string, object?> Arguments, string Entry)?> PrepareAsync(ActiveJob job, string folder, CancellationToken stopping)
    {
        var assignment = job.Assignment;
        byte[] content;
        try
        {
            content = await _connection.DownloadPackageAsync(assignment.JobId, assignment.LeaseId, stopping).ConfigureAwait(false);
        }
        catch (RunnerProtocolException ex) when (ex.Status is System.Net.HttpStatusCode.NotFound)
        {
            LogNotStarted(_logger, assignment.JobId, "the job is no longer assigned to this Runner");
            return null;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            await _connection.ReleaseAsync(assignment.JobId, new JobReleaseRequest(assignment.LeaseId, "The Runner is shutting down."), CancellationToken.None).ConfigureAwait(false);
            return null;
        }

        if (!WorkflowPackage.TryVerify(content, assignment.PackageSha256, out var package, out var integrityError))
        {
            await NotStartedAsync(job, OrchestrationErrorCodes.PackageIntegrity, integrityError!, "PackageIntegrity").ConfigureAwait(false);
            return null;
        }

        if (MissingDependencies(package!.Manifest) is { Count: > 0 } missing)
        {
            await NotStartedAsync(job, OrchestrationErrorCodes.DependencyMissing, "This Runner lacks what the package needs: " + string.Join("; ", missing) + ".", "DependencyMissing").ConfigureAwait(false);
            return null;
        }

        var root = Path.GetFullPath(folder);
        Directory.CreateDirectory(root);
        foreach (var file in package.Files)
        {
            var target = Path.GetFullPath(Path.Combine(root, file.Path));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                await NotStartedAsync(job, OrchestrationErrorCodes.PackageIntegrity, $"The package path '{file.Path}' leaves the job folder.", "PackageIntegrity").ConfigureAwait(false);
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, file.Content, new UTF8Encoding(false), stopping).ConfigureAwait(false);
        }

        if (WorkflowPackage.FindEntryPoint(package.Manifest, assignment.EntryPath) is null
            || package.Files.FirstOrDefault(f => f.Path == assignment.EntryPath) is not { } entry)
        {
            await NotStartedAsync(job, OrchestrationErrorCodes.PackageIntegrity, $"The package has no entry point '{assignment.EntryPath}'.", "PackageIntegrity").ConfigureAwait(false);
            return null;
        }

        var loaded = _loader.Load(entry.Content);
        if (loaded.Workflow is not { } workflow)
        {
            var first = loaded.Errors.FirstOrDefault();
            await NotStartedAsync(job, OrchestrationErrorCodes.PreparationFailed, $"The workflow is not valid on this Runner: {first?.Code} {first?.Message}".Trim(), "PreparationFailed").ConfigureAwait(false);
            return null;
        }

        if (!TryArguments(workflow, assignment.ArgumentsJson, out var arguments, out var argumentError))
        {
            await NotStartedAsync(job, ExecutionErrorCodes.InvalidArguments, argumentError, "Arguments").ConfigureAwait(false);
            return null;
        }

        return (workflow, arguments, Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
    }

    private async Task PumpEventsAsync(ActiveJob job, ExecutionHandle handle)
    {
        var queue = new ConcurrentQueue<ExecutionEventMessage>();
        var reader = Task.Run(async () =>
        {
            await foreach (var message in handle.ReadEventsAsync(0, CancellationToken.None).ConfigureAwait(false))
            {
                if (message.Sequence > 0)
                {
                    queue.Enqueue(message);
                }
            }
        });

        while (true)
        {
            var done = reader.IsCompleted;
            await FlushAsync(job, handle, queue).ConfigureAwait(false);
            if (done && (queue.IsEmpty || job.Abandoned))
            {
                break;
            }

            if (!done)
            {
                await Task.WhenAny(reader, Task.Delay(_flushInterval, _time)).ConfigureAwait(false);
            }
            else
            {
                // Events still queued after a failed send: retry after a pause.
                await Task.Delay(TimeSpan.FromSeconds(1), _time).ConfigureAwait(false);
            }
        }

        await reader.ConfigureAwait(false);
    }

    private async Task FlushAsync(ActiveJob job, ExecutionHandle handle, ConcurrentQueue<ExecutionEventMessage> queue)
    {
        while (!queue.IsEmpty && !job.Abandoned)
        {
            var batch = new List<ExecutionEventMessage>();
            while (batch.Count < RunnerProtocol.MaxEventsPerRequest && queue.TryDequeue(out var next))
            {
                batch.Add(next);
            }

            try
            {
                var answer = await _connection.SendEventsAsync(job.Assignment.JobId, new JobEventsRequest { LeaseId = job.Assignment.LeaseId, Events = batch }, CancellationToken.None).ConfigureAwait(false);
                if (!answer.LeaseValid)
                {
                    LogLeaseLost(_logger, job.Assignment.JobId);
                    job.Abandoned = true;
                    _host.Cancel(handle.RunId);
                    return;
                }

                if (answer.CancelRequested && !job.CancelRequested)
                {
                    job.CancelRequested = true;
                    _host.Cancel(handle.RunId);
                }
            }
            catch (RunnerProtocolException ex)
            {
                // At-least-once: the Server ignores events it already has, so a batch whose answer was lost is resent.
                LogEventsDelayed(_logger, job.Assignment.JobId, ex.Message);
                foreach (var message in batch)
                {
                    queue.Enqueue(message);
                }

                return;
            }
        }
    }

    private async Task ReportAsync(ActiveJob job, WorkflowDefinition workflow, WorkflowExecutionResult? result, string? hostFailure)
    {
        JobCompleteRequest request;
        if (result is null)
        {
            request = new JobCompleteRequest
            {
                LeaseId = job.Assignment.LeaseId,
                Status = nameof(ExecutionStatus.Failed),
                Error = new ExecutionErrorMessage(ExecutionErrorCodes.ActivityFailed, $"The Runner's execution host failed: {hostFailure}", ErrorType: "HostFailure"),
            };
        }
        else
        {
            var error = result.Error is { } e
                ? new ExecutionErrorMessage(e.Code, job.ShuttingDown && result.Status == ExecutionStatus.Cancelled ? "Cancelled: the Runner shut down while the job ran." : e.Message, e.NodeId, e.ActivityType, e.ErrorType)
                : null;
            request = new JobCompleteRequest
            {
                LeaseId = job.Assignment.LeaseId,
                Status = result.Status.ToString(),
                Error = error,
                OutputsJson = result.Status == ExecutionStatus.Succeeded ? Outputs(workflow, result.Outputs) : null,
                DurationMs = result.Duration.TotalMilliseconds,
                ExecutionId = result.ExecutionId.ToString(),
            };
        }

        var answer = await _connection.CompleteAsync(job.Assignment.JobId, request, CancellationToken.None).ConfigureAwait(false);
        LogReported(_logger, job.Assignment.JobId, request.Status, answer.LeaseValid, answer.Duplicate);
    }

    private async Task NotStartedAsync(ActiveJob job, string code, string message, string errorType)
    {
        LogNotStarted(_logger, job.Assignment.JobId, message);
        await _connection.CompleteAsync(job.Assignment.JobId, new JobCompleteRequest
        {
            LeaseId = job.Assignment.LeaseId,
            Status = nameof(ExecutionStatus.Failed),
            NotStarted = true,
            Error = new ExecutionErrorMessage(code, message, ErrorType: errorType),
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private static bool TryArguments(WorkflowDefinition workflow, string json, out IReadOnlyDictionary<string, object?> arguments, out string error)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        arguments = values;
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var definition = workflow.Arguments.FirstOrDefault(a => a.Name == property.Name && a.IsInput);
                if (definition is null)
                {
                    error = $"'{property.Name}' is not an input argument of the workflow.";
                    return false;
                }

                if (!WorkflowValues.TryFromJson(property.Value, definition.Type, out var value, out var conversion))
                {
                    error = $"Argument '{property.Name}': {conversion}";
                    return false;
                }

                values[property.Name] = value;
            }
        }
        catch (JsonException ex)
        {
            error = $"The job's arguments are not valid JSON: {ex.Message}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailed(_logger, folder, ex.Message);
        }
    }

    [LoggerMessage(EventId = 6100, Level = LogLevel.Information, Message = "Job {JobId}: {Process} v{Version} received (correlation {CorrelationId})")]
    private static partial void LogJobReceived(ILogger logger, string jobId, string process, int version, string correlationId);

    [LoggerMessage(EventId = 6101, Level = LogLevel.Warning, Message = "Job {JobId} not started: {Reason}")]
    private static partial void LogNotStarted(ILogger logger, string jobId, string reason);

    [LoggerMessage(EventId = 6102, Level = LogLevel.Information, Message = "Job {JobId} reported {Status} (lease valid {LeaseValid}, duplicate {Duplicate})")]
    private static partial void LogReported(ILogger logger, string jobId, string status, bool leaseValid, bool duplicate);

    [LoggerMessage(EventId = 6103, Level = LogLevel.Warning, Message = "Job {JobId}: the Server no longer recognizes this Runner's lease; the run was stopped")]
    private static partial void LogLeaseLost(ILogger logger, string jobId);

    [LoggerMessage(EventId = 6104, Level = LogLevel.Warning, Message = "Job {JobId}: events delayed ({Reason}); they will be resent")]
    private static partial void LogEventsDelayed(ILogger logger, string jobId, string reason);

    [LoggerMessage(EventId = 6105, Level = LogLevel.Error, Message = "Job {JobId}: the outcome could not be reported ({Reason}); the Server will mark it interrupted")]
    private static partial void LogReportFailed(ILogger logger, string jobId, string reason);

    [LoggerMessage(EventId = 6106, Level = LogLevel.Error, Message = "Job {JobId}: the Server refused this Runner's token")]
    private static partial void LogAuthLost(ILogger logger, string jobId);

    [LoggerMessage(EventId = 6107, Level = LogLevel.Warning, Message = "The work folder {Folder} could not be removed: {Reason}")]
    private static partial void LogCleanupFailed(ILogger logger, string folder, string reason);

    [LoggerMessage(EventId = 6108, Level = LogLevel.Warning, Message = "{Count} job(s) did not stop within the shutdown grace period")]
    private static partial void LogShutdownTimeout(ILogger logger, int count);

    private sealed class ActiveJob(JobAssignment assignment)
    {
        private volatile string? _runId;
        private volatile bool _cancelRequested;
        private volatile bool _abandoned;
        private volatile bool _shuttingDown;

        public JobAssignment Assignment { get; } = assignment;

        public Task? Task { get; set; }

        public string? RunId
        {
            get => _runId;
            set => _runId = value;
        }

        public bool CancelRequested
        {
            get => _cancelRequested;
            set => _cancelRequested = value;
        }

        public bool Abandoned
        {
            get => _abandoned;
            set => _abandoned = value;
        }

        public bool ShuttingDown
        {
            get => _shuttingDown;
            set => _shuttingDown = value;
        }
    }
}
