using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MyRPA.Orchestration.Sqlite;

/// <summary>
/// The orchestrator's store on one SQLite file (ADR-0050). Records are JSON documents with indexed columns for the
/// queries the orchestrator makes. One connection serves the Server process (writes are serialized anyway); WAL
/// journaling keeps it durable across crashes. Revisioned updates are conditional
/// (<c>WHERE revision = @expected</c>); published packages are protected by triggers against any change or deletion.
/// </summary>
public sealed class SqliteOrchestrationStore : IOrchestrationStore
{
    /// <summary>The schema version this store creates.</summary>
    public const int SchemaVersion = 2;

    private static readonly string[] _tables = ["processes", "packages", "jobs", "job_events", "runners", "schedules", "access_tokens", "audit"];

    private readonly string _path;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _disposed;

    /// <summary>Opens (creating if needed) the store at <paramref name="path"/>.</summary>
    public SqliteOrchestrationStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken) => RunAsync(() =>
    {
        if (_connection.State != System.Data.ConnectionState.Open)
        {
            _connection.Open();
        }

        Execute("PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA busy_timeout = 5000;");
        var version = Convert.ToInt32(Scalar("PRAGMA user_version;"), System.Globalization.CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException($"The orchestration store {_path} has schema {version}; this Server knows up to {SchemaVersion}.");
        }

        if (version < 1)
        {
            using var transaction = _connection.BeginTransaction();
            Execute("""
                CREATE TABLE runners (id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, revision INTEGER NOT NULL, json TEXT NOT NULL);
                CREATE TABLE access_tokens (id TEXT PRIMARY KEY, revision INTEGER NOT NULL, json TEXT NOT NULL);
                CREATE TABLE audit (id INTEGER PRIMARY KEY AUTOINCREMENT, time_ms INTEGER NOT NULL, actor TEXT NOT NULL, action TEXT NOT NULL, target TEXT, details TEXT);
                CREATE TABLE settings (key TEXT PRIMARY KEY, json TEXT NOT NULL);
                """, transaction);
            CreateProcessTables(transaction);
            Execute($"PRAGMA user_version = {SchemaVersion};", transaction);
            transaction.Commit();
        }
        else if (version < 2)
        {
            // Schema 2 (ADR-0049 amendment): a process is a project and a package has entry points. Schema 1 held one
            // process per workflow and was never released, so its processes, packages, jobs and schedules are dropped;
            // Runners, access tokens, audit and settings stay. Dropping a table fires no triggers.
            using var transaction = _connection.BeginTransaction();
            Execute("""
                DROP TABLE job_events;
                DROP TABLE jobs;
                DROP TABLE schedules;
                DROP TABLE packages;
                DROP TABLE processes;
                """, transaction);
            CreateProcessTables(transaction);
            Execute($"PRAGMA user_version = {SchemaVersion};", transaction);
            transaction.Commit();
        }

        return true;
    }, cancellationToken);

    private void CreateProcessTables(SqliteTransaction transaction) => Execute("""
        CREATE TABLE processes (id TEXT PRIMARY KEY, project TEXT NOT NULL UNIQUE, revision INTEGER NOT NULL, json TEXT NOT NULL);
        CREATE TABLE packages (process_id TEXT NOT NULL, version INTEGER NOT NULL, sha256 TEXT NOT NULL, json TEXT NOT NULL, content BLOB NOT NULL, PRIMARY KEY (process_id, version));
        CREATE TRIGGER packages_no_update BEFORE UPDATE ON packages BEGIN SELECT RAISE(ABORT, 'published packages are immutable'); END;
        CREATE TRIGGER packages_no_delete BEFORE DELETE ON packages BEGIN SELECT RAISE(ABORT, 'published packages are immutable'); END;
        CREATE TABLE jobs (id TEXT PRIMARY KEY, state TEXT NOT NULL, process_id TEXT NOT NULL, runner_id TEXT, schedule_id TEXT, idempotency_key TEXT UNIQUE,
            created_ms INTEGER NOT NULL, completed_ms INTEGER, revision INTEGER NOT NULL, json TEXT NOT NULL);
        CREATE INDEX jobs_state ON jobs (state);
        CREATE INDEX jobs_created ON jobs (created_ms);
        CREATE INDEX jobs_process ON jobs (process_id, created_ms);
        CREATE INDEX jobs_completed ON jobs (completed_ms);
        CREATE TABLE job_events (job_id TEXT NOT NULL, attempt INTEGER NOT NULL, sequence INTEGER NOT NULL, kind TEXT NOT NULL, time_ms INTEGER NOT NULL, json TEXT NOT NULL,
            PRIMARY KEY (job_id, attempt, sequence));
        CREATE TABLE schedules (id TEXT PRIMARY KEY, revision INTEGER NOT NULL, json TEXT NOT NULL);
        """, transaction);

    /// <inheritdoc />
    public Task<ProcessRecord?> GetProcessAsync(string processId, CancellationToken cancellationToken) =>
        RunAsync(() => One("SELECT json, revision FROM processes WHERE id = $id", OrchestrationJson.Default.ProcessRecord, WithRevision, ("$id", processId)), cancellationToken);

    /// <inheritdoc />
    public Task<ProcessRecord?> FindProcessAsync(string project, CancellationToken cancellationToken) =>
        RunAsync(() => One("SELECT json, revision FROM processes WHERE project = $p", OrchestrationJson.Default.ProcessRecord, WithRevision, ("$p", project)), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ProcessRecord>> ListProcessesAsync(CancellationToken cancellationToken) =>
        ManyAsync("SELECT json, revision FROM processes ORDER BY id", OrchestrationJson.Default.ProcessRecord, WithRevision, cancellationToken);

    /// <inheritdoc />
    public Task<bool> InsertProcessAsync(ProcessRecord process, CancellationToken cancellationToken) => RunAsync(() =>
    {
        ArgumentNullException.ThrowIfNull(process);
        var record = process with { Revision = 0 };
        return TryInsert(
            "INSERT INTO processes (id, project, revision, json) VALUES ($id, $p, 0, $json)",
            ("$id", record.ProcessId), ("$p", record.Project), ("$json", Json(record, OrchestrationJson.Default.ProcessRecord)));
    }, cancellationToken);

    /// <inheritdoc />
    public Task<bool> UpdateProcessAsync(ProcessRecord process, CancellationToken cancellationToken) => RunAsync(() =>
    {
        ArgumentNullException.ThrowIfNull(process);
        var next = process with { Revision = process.Revision + 1 };
        return Execute(
            "UPDATE processes SET json = $json, revision = $next WHERE id = $id AND revision = $expected",
            null, ("$json", Json(next, OrchestrationJson.Default.ProcessRecord)), ("$next", next.Revision), ("$id", process.ProcessId), ("$expected", process.Revision)) == 1;
    }, cancellationToken);

    /// <inheritdoc />
    public Task<bool> InsertPackageAsync(PackageRecord package, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => RunAsync(() =>
    {
        ArgumentNullException.ThrowIfNull(package);
        return TryInsert(
            "INSERT INTO packages (process_id, version, sha256, json, content) VALUES ($id, $v, $sha, $json, $content)",
            ("$id", package.ProcessId), ("$v", package.Version), ("$sha", package.Sha256), ("$json", Json(package, OrchestrationJson.Default.PackageRecord)), ("$content", content.ToArray()));
    }, cancellationToken);

    /// <inheritdoc />
    public Task<PackageRecord?> GetPackageAsync(string processId, int version, CancellationToken cancellationToken) =>
        RunAsync(() => One("SELECT json, 0 FROM packages WHERE process_id = $id AND version = $v", OrchestrationJson.Default.PackageRecord, (r, _) => r, ("$id", processId), ("$v", version)), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<PackageRecord>> ListPackagesAsync(string processId, CancellationToken cancellationToken) =>
        ManyAsync("SELECT json, 0 FROM packages WHERE process_id = $id ORDER BY version DESC", OrchestrationJson.Default.PackageRecord, (r, _) => r, ("$id", processId), cancellationToken);

    /// <inheritdoc />
    public Task<byte[]?> ReadPackageContentAsync(string processId, int version, CancellationToken cancellationToken) => RunAsync(() =>
    {
        using var command = Command("SELECT content FROM packages WHERE process_id = $id AND version = $v", null, ("$id", processId), ("$v", version));
        return command.ExecuteScalar() as byte[];
    }, cancellationToken);

    /// <inheritdoc />
    public Task<bool> InsertJobAsync(JobRecord job, CancellationToken cancellationToken) => RunAsync(() => InsertJob(job, null), cancellationToken);

    /// <inheritdoc />
    public Task<JobRecord?> GetJobAsync(string jobId, CancellationToken cancellationToken) =>
        RunAsync(() => One("SELECT json, revision FROM jobs WHERE id = $id", OrchestrationJson.Default.JobRecord, WithRevision, ("$id", jobId)), cancellationToken);

    /// <inheritdoc />
    public Task<JobRecord?> FindJobByIdempotencyKeyAsync(string key, CancellationToken cancellationToken) =>
        RunAsync(() => One("SELECT json, revision FROM jobs WHERE idempotency_key = $k", OrchestrationJson.Default.JobRecord, WithRevision, ("$k", key)), cancellationToken);

    /// <inheritdoc />
    public Task<bool> UpdateJobAsync(JobRecord job, CancellationToken cancellationToken) => RunAsync(() =>
    {
        ArgumentNullException.ThrowIfNull(job);
        var next = job with { Revision = job.Revision + 1 };
        return Execute(
            "UPDATE jobs SET json = $json, state = $state, runner_id = $runner, completed_ms = $completed, revision = $next WHERE id = $id AND revision = $expected",
            null,
            ("$json", Json(next, OrchestrationJson.Default.JobRecord)),
            ("$state", next.State.ToString()),
            ("$runner", next.RunnerId),
            ("$completed", next.CompletedAt?.ToUnixTimeMilliseconds()),
            ("$next", next.Revision),
            ("$id", job.JobId),
            ("$expected", job.Revision)) == 1;
    }, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<JobRecord>> ListActiveJobsAsync(CancellationToken cancellationToken) =>
        ManyAsync("SELECT json, revision FROM jobs WHERE state IN ('Queued', 'Assigned', 'Running') ORDER BY created_ms", OrchestrationJson.Default.JobRecord, WithRevision, cancellationToken);

    /// <inheritdoc />
    public Task<JobPage> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken) => RunAsync(() =>
    {
        ArgumentNullException.ThrowIfNull(query);
        var where = new List<string>();
        var parameters = new List<(string, object?)>();
        if (query.States.Count > 0)
        {
            var names = query.States.Distinct().Select((s, i) => (Name: $"$s{i}", Value: s.ToString())).ToList();
            where.Add($"state IN ({string.Join(", ", names.Select(n => n.Name))})");
            parameters.AddRange(names.Select(n => (n.Name, (object?)n.Value)));
        }

        Add(where, parameters, "process_id = $process", "$process", query.ProcessId);
        Add(where, parameters, "runner_id = $runner", "$runner", query.RunnerId);
        Add(where, parameters, "schedule_id = $schedule", "$schedule", query.ScheduleId);
        if (query.Since is { } since)
        {
            where.Add("created_ms >= $since");
            parameters.Add(("$since", since.ToUnixTimeMilliseconds()));
        }

        var filter = where.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", where);
        var total = Convert.ToInt32(Scalar("SELECT COUNT(*) FROM jobs" + filter, [.. parameters]), System.Globalization.CultureInfo.InvariantCulture);
        parameters.Add(("$limit", Math.Clamp(query.Limit, 1, 200)));
        parameters.Add(("$offset", Math.Max(0, query.Offset)));
        var jobs = Many($"SELECT json, revision FROM jobs{filter} ORDER BY created_ms DESC, id DESC LIMIT $limit OFFSET $offset", OrchestrationJson.Default.JobRecord, WithRevision, [.. parameters]);
        return new JobPage(jobs, total);
    }, cancellationToken);

    /// <inheritdoc />
    public Task<JobStats> GetStatsAsync(DateTimeOffset since, TimeSpan bucket, CancellationToken cancellationToken) => RunAsync(() =>
    {
        var byState = Enum.GetValues<JobState>().ToDictionary(s => s, _ => 0);
        using (var command = Command("SELECT state, COUNT(*) FROM jobs GROUP BY state", null))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (Enum.TryParse<JobState>(reader.GetString(0), out var state))
                {
                    byState[state] = reader.GetInt32(1);
                }
            }
        }

        var start = since.ToUnixTimeMilliseconds();
        var size = Math.Max(1, (long)bucket.TotalMilliseconds);
        var buckets = new SortedDictionary<long, int[]>();
        using (var command = Command(
            "SELECT (completed_ms - $start) / $size, state, COUNT(*) FROM jobs WHERE completed_ms >= $start GROUP BY 1, 2",
            null, ("$start", start), ("$size", size)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var index = reader.GetInt64(0);
                var counts = buckets.TryGetValue(index, out var existing) ? existing : buckets[index] = new int[4];
                var slot = reader.GetString(1) switch
                {
                    nameof(JobState.Succeeded) => 0,
                    nameof(JobState.Failed) or nameof(JobState.TimedOut) => 1,
                    nameof(JobState.Cancelled) => 2,
                    nameof(JobState.Interrupted) => 3,
                    _ => -1,
                };
                if (slot >= 0)
                {
                    counts[slot] += reader.GetInt32(2);
                }
            }
        }

        return new JobStats(
            byState,
            [.. buckets.Select(b => new JobStatsBucket(DateTimeOffset.FromUnixTimeMilliseconds(start + (b.Key * size)), b.Value[0], b.Value[1], b.Value[2], b.Value[3]))]);
    }, cancellationToken);

    /// <inheritdoc />
    public Task<int> AppendEventsAsync(string jobId, IReadOnlyList<StoredEvent> events, CancellationToken cancellationToken) => RunAsync(() =>
    {
        ArgumentNullException.ThrowIfNull(events);
        using var transaction = _connection.BeginTransaction();
        var added = 0;
        foreach (var e in events)
        {
            // At-least-once delivery: an event already stored (same attempt and sequence) is ignored.
            added += Execute(
                "INSERT OR IGNORE INTO job_events (job_id, attempt, sequence, kind, time_ms, json) VALUES ($job, $attempt, $seq, $kind, $time, $json)",
                transaction, ("$job", jobId), ("$attempt", e.Attempt), ("$seq", e.Sequence), ("$kind", e.Kind), ("$time", e.Time.ToUnixTimeMilliseconds()), ("$json", e.Json));
        }

        transaction.Commit();
        return added;
    }, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredEvent>> ReadEventsAsync(string jobId, int afterAttempt, long afterSequence, int limit, CancellationToken cancellationToken) => RunAsync(() =>
    {
        using var command = Command(
            "SELECT attempt, sequence, kind, time_ms, json FROM job_events WHERE job_id = $job AND (attempt > $a OR (attempt = $a AND sequence > $s)) ORDER BY attempt, sequence LIMIT $limit",
            null, ("$job", jobId), ("$a", afterAttempt), ("$s", afterSequence), ("$limit", Math.Clamp(limit, 1, 1000)));
        using var reader = command.ExecuteReader();
        var result = new List<StoredEvent>();
        while (reader.Read())
        {
            result.Add(new StoredEvent(reader.GetInt32(0), reader.GetInt64(1), reader.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)), reader.GetString(4)));
        }

        return (IReadOnlyList<StoredEvent>)result;
    }, cancellationToken);

    /// <inheritdoc />
    public Task<RunnerRecord?> GetRunnerAsync(string runnerId, CancellationToken cancellationToken) =>
        RunAsync(() => One("SELECT json, revision FROM runners WHERE id = $id", OrchestrationJson.Default.RunnerRecord, WithRevision, ("$id", runnerId)), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<RunnerRecord>> ListRunnersAsync(CancellationToken cancellationToken) =>
        ManyAsync("SELECT json, revision FROM runners ORDER BY name", OrchestrationJson.Default.RunnerRecord, WithRevision, cancellationToken);

    /// <inheritdoc />
    public Task<bool> InsertRunnerAsync(RunnerRecord runner, CancellationToken cancellationToken) => RunAsync(() =>
    {
        ArgumentNullException.ThrowIfNull(runner);
        var record = runner with { Revision = 0 };
        return TryInsert("INSERT INTO runners (id, name, revision, json) VALUES ($id, $name, 0, $json)",
            ("$id", record.RunnerId), ("$name", record.Name), ("$json", Json(record, OrchestrationJson.Default.RunnerRecord)));
    }, cancellationToken);

    /// <inheritdoc />
    public Task<bool> UpdateRunnerAsync(RunnerRecord runner, CancellationToken cancellationToken) => RunAsync(() =>
    {
        ArgumentNullException.ThrowIfNull(runner);
        var next = runner with { Revision = runner.Revision + 1 };
        return Execute("UPDATE runners SET json = $json, name = $name, revision = $next WHERE id = $id AND revision = $expected",
            null, ("$json", Json(next, OrchestrationJson.Default.RunnerRecord)), ("$name", next.Name), ("$next", next.Revision), ("$id", runner.RunnerId), ("$expected", runner.Revision)) == 1;
    }, cancellationToken);

    /// <inheritdoc />
    public Task<ScheduleRecord?> GetScheduleAsync(string scheduleId, CancellationToken cancellationToken) =>
        RunAsync(() => One("SELECT json, revision FROM schedules WHERE id = $id", OrchestrationJson.Default.ScheduleRecord, WithRevision, ("$id", scheduleId)), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ScheduleRecord>> ListSchedulesAsync(CancellationToken cancellationToken) =>
        ManyAsync("SELECT json, revision FROM schedules ORDER BY id", OrchestrationJson.Default.ScheduleRecord, WithRevision, cancellationToken);

    /// <inheritdoc />
    public Task<bool> InsertScheduleAsync(ScheduleRecord schedule, CancellationToken cancellationToken) => RunAsync(() =>
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var record = schedule with { Revision = 0 };
        return TryInsert("INSERT INTO schedules (id, revision, json) VALUES ($id, 0, $json)", ("$id", record.ScheduleId), ("$json", Json(record, OrchestrationJson.Default.ScheduleRecord)));
    }, cancellationToken);

    /// <inheritdoc />
    public Task<bool> UpdateScheduleAsync(ScheduleRecord schedule, CancellationToken cancellationToken) => RunAsync(() => UpdateSchedule(schedule, null), cancellationToken);

    /// <inheritdoc />
    public Task<bool> DeleteScheduleAsync(string scheduleId, CancellationToken cancellationToken) =>
        RunAsync(() => Execute("DELETE FROM schedules WHERE id = $id", null, ("$id", scheduleId)) == 1, cancellationToken);

    /// <inheritdoc />
    public Task<bool> FireScheduleAsync(ScheduleRecord schedule, JobRecord? job, CancellationToken cancellationToken) => RunAsync(() =>
    {
        ArgumentNullException.ThrowIfNull(schedule);
        using var transaction = _connection.BeginTransaction();
        if ((job is not null && !InsertJob(job, transaction)) || !UpdateSchedule(schedule, transaction))
        {
            transaction.Rollback();
            return false;
        }

        transaction.Commit();
        return true;
    }, cancellationToken);

    /// <inheritdoc />
    public Task<AccessTokenRecord?> GetAccessTokenAsync(string tokenId, CancellationToken cancellationToken) =>
        RunAsync(() => One("SELECT json, revision FROM access_tokens WHERE id = $id", OrchestrationJson.Default.AccessTokenRecord, WithRevision, ("$id", tokenId)), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<AccessTokenRecord>> ListAccessTokensAsync(CancellationToken cancellationToken) =>
        ManyAsync("SELECT json, revision FROM access_tokens ORDER BY id", OrchestrationJson.Default.AccessTokenRecord, WithRevision, cancellationToken);

    /// <inheritdoc />
    public Task<bool> InsertAccessTokenAsync(AccessTokenRecord token, CancellationToken cancellationToken) => RunAsync(() =>
    {
        ArgumentNullException.ThrowIfNull(token);
        var record = token with { Revision = 0 };
        return TryInsert("INSERT INTO access_tokens (id, revision, json) VALUES ($id, 0, $json)", ("$id", record.TokenId), ("$json", Json(record, OrchestrationJson.Default.AccessTokenRecord)));
    }, cancellationToken);

    /// <inheritdoc />
    public Task<bool> UpdateAccessTokenAsync(AccessTokenRecord token, CancellationToken cancellationToken) => RunAsync(() =>
    {
        ArgumentNullException.ThrowIfNull(token);
        var next = token with { Revision = token.Revision + 1 };
        return Execute("UPDATE access_tokens SET json = $json, revision = $next WHERE id = $id AND revision = $expected",
            null, ("$json", Json(next, OrchestrationJson.Default.AccessTokenRecord)), ("$next", next.Revision), ("$id", token.TokenId), ("$expected", token.Revision)) == 1;
    }, cancellationToken);

    /// <inheritdoc />
    public Task AppendAuditAsync(DateTimeOffset time, string actor, string action, string? target, string? details, CancellationToken cancellationToken) => RunWriteAsync(() =>
        Execute("INSERT INTO audit (time_ms, actor, action, target, details) VALUES ($t, $actor, $action, $target, $details)",
            null, ("$t", time.ToUnixTimeMilliseconds()), ("$actor", actor), ("$action", action), ("$target", target), ("$details", details)), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int offset, int limit, CancellationToken cancellationToken) => RunAsync(() =>
    {
        using var command = Command("SELECT id, time_ms, actor, action, target, details FROM audit ORDER BY id DESC LIMIT $limit OFFSET $offset", null, ("$limit", limit), ("$offset", offset));
        using var reader = command.ExecuteReader();
        var result = new List<AuditEntry>();
        while (reader.Read())
        {
            result.Add(new AuditEntry(reader.GetInt64(0), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return (IReadOnlyList<AuditEntry>)result;
    }, cancellationToken);

    /// <inheritdoc />
    public Task<OrchestrationPolicy> GetPolicyAsync(CancellationToken cancellationToken) => RunAsync(() =>
    {
        using var command = Command("SELECT json FROM settings WHERE key = 'policy'", null);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize(json, OrchestrationJson.Default.OrchestrationPolicy) ?? new OrchestrationPolicy() : new OrchestrationPolicy();
    }, cancellationToken);

    /// <inheritdoc />
    public Task SavePolicyAsync(OrchestrationPolicy policy, CancellationToken cancellationToken) => RunWriteAsync(() =>
        Execute("INSERT INTO settings (key, json) VALUES ('policy', $json) ON CONFLICT (key) DO UPDATE SET json = excluded.json",
            null, ("$json", Json(policy, OrchestrationJson.Default.OrchestrationPolicy))), cancellationToken);

    /// <inheritdoc />
    public Task<StoreDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken) => RunAsync(() =>
    {
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in _tables)
        {
            counts[table] = Convert.ToInt64(Scalar($"SELECT COUNT(*) FROM {table}"), System.Globalization.CultureInfo.InvariantCulture);
        }

        var size = new[] { _path, _path + "-wal" }.Where(File.Exists).Sum(p => new FileInfo(p).Length);
        var version = Convert.ToInt32(Scalar("PRAGMA user_version;"), System.Globalization.CultureInfo.InvariantCulture);
        return new StoreDiagnostics("SQLite", _path, size, version, counts);
    }, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }

    private bool InsertJob(JobRecord job, SqliteTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(job);
        var record = job with { Revision = 0 };
        try
        {
            return Execute(
                "INSERT INTO jobs (id, state, process_id, runner_id, schedule_id, idempotency_key, created_ms, completed_ms, revision, json) VALUES ($id, $state, $process, $runner, $schedule, $key, $created, $completed, 0, $json)",
                transaction,
                ("$id", record.JobId),
                ("$state", record.State.ToString()),
                ("$process", record.ProcessId),
                ("$runner", record.RunnerId),
                ("$schedule", record.ScheduleId),
                ("$key", record.IdempotencyKey),
                ("$created", record.CreatedAt.ToUnixTimeMilliseconds()),
                ("$completed", record.CompletedAt?.ToUnixTimeMilliseconds()),
                ("$json", Json(record, OrchestrationJson.Default.JobRecord))) == 1;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // SQLITE_CONSTRAINT: the id or the idempotency key exists.
            return false;
        }
    }

    private bool UpdateSchedule(ScheduleRecord schedule, SqliteTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var next = schedule with { Revision = schedule.Revision + 1 };
        return Execute("UPDATE schedules SET json = $json, revision = $next WHERE id = $id AND revision = $expected",
            transaction, ("$json", Json(next, OrchestrationJson.Default.ScheduleRecord)), ("$next", next.Revision), ("$id", schedule.ScheduleId), ("$expected", schedule.Revision)) == 1;
    }

    private bool TryInsert(string sql, params (string Name, object? Value)[] parameters)
    {
        try
        {
            return Execute(sql, null, parameters) == 1;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    private async Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return action();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RunWriteAsync(Func<int> action, CancellationToken cancellationToken) => await RunAsync(action, cancellationToken).ConfigureAwait(false);

    private SqliteCommand Command(string sql, SqliteTransaction? transaction, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private int Execute(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        return command.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, null, parameters);
        return command.ExecuteScalar();
    }

    private T? One<T>(string sql, JsonTypeInfo<T> type, Func<T, long, T> withRevision, params (string Name, object? Value)[] parameters)
        where T : class =>
        Many(sql, type, withRevision, parameters) is { Count: > 0 } found ? found[0] : null;

    private Task<IReadOnlyList<T>> ManyAsync<T>(string sql, JsonTypeInfo<T> type, Func<T, long, T> withRevision, (string Name, object? Value) parameter, CancellationToken cancellationToken)
        where T : class =>
        RunAsync<IReadOnlyList<T>>(() => Many(sql, type, withRevision, parameter), cancellationToken);

    private Task<IReadOnlyList<T>> ManyAsync<T>(string sql, JsonTypeInfo<T> type, Func<T, long, T> withRevision, CancellationToken cancellationToken)
        where T : class =>
        RunAsync<IReadOnlyList<T>>(() => Many(sql, type, withRevision), cancellationToken);

    private List<T> Many<T>(string sql, JsonTypeInfo<T> type, Func<T, long, T> withRevision, params (string Name, object? Value)[] parameters)
        where T : class
    {
        using var command = Command(sql, null, parameters);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
        {
            var record = JsonSerializer.Deserialize(reader.GetString(0), type) ?? throw new InvalidDataException("A stored record is empty.");
            result.Add(withRevision(record, reader.GetInt64(1)));
        }

        return result;
    }

    private static string Json<T>(T value, JsonTypeInfo<T> type) => JsonSerializer.Serialize(value, type);

    private static void Add(List<string> where, List<(string, object?)> parameters, string clause, string name, string? value)
    {
        if (value is not null)
        {
            where.Add(clause);
            parameters.Add((name, value));
        }
    }

    private static ProcessRecord WithRevision(ProcessRecord r, long revision) => r with { Revision = revision };

    private static JobRecord WithRevision(JobRecord r, long revision) => r with { Revision = revision };

    private static RunnerRecord WithRevision(RunnerRecord r, long revision) => r with { Revision = revision };

    private static ScheduleRecord WithRevision(ScheduleRecord r, long revision) => r with { Revision = revision };

    private static AccessTokenRecord WithRevision(AccessTokenRecord r, long revision) => r with { Revision = revision };
}

/// <summary>Registers the SQLite store.</summary>
public static class SqliteOrchestrationServiceCollectionExtensions
{
    /// <summary>Adds the SQLite store at <paramref name="path"/> (the host initializes it at startup).</summary>
    public static IServiceCollection AddMyRpaSqliteOrchestrationStore(this IServiceCollection services, string path)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IOrchestrationStore>(_ => new SqliteOrchestrationStore(path));
        return services;
    }
}
