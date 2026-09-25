// W0 spike: prototype of the MyRPA.Server execution API. Throwaway; see ../README.md.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;
using MyRPA.Activities;
using MyRPA.Core.Activities;
using MyRPA.Core.Diagnostics;
using MyRPA.Core.Identifiers;
using MyRPA.Runtime;
using MyRPA.Storage;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Serialization;
using MyRPA.Workflow.Validation;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5199");
builder.Logging.ClearProviders();
builder.Services.AddSingleton<ExecutionHub>();
builder.Services.AddSingleton<ILoggerProvider>(sp => new ExecutionLogRouter(sp.GetRequiredService<ExecutionHub>()));
builder.Services.AddMyRpaRuntime().AddMyRpaActivities().AddMyRpaStorage();
var app = builder.Build();

var web = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "web", "dist"));
if (Directory.Exists(web))
{
    var files = new PhysicalFileProvider(web);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}

app.MapGet("/api/activities", (IActivityCatalog catalog) => Results.Text(ActivityCatalogJson.Write(catalog.Descriptors), "application/json"));

// Validation: the authoritative WorkflowLoader, timed on the server so client round trips can be split into server and network time.
app.MapPost("/api/validate", async (HttpRequest request, WorkflowLoader loader, HttpResponse response) =>
{
    using var reader = new StreamReader(request.Body);
    var json = await reader.ReadToEndAsync();
    var clock = Stopwatch.StartNew();
    var result = loader.Load(json);
    var serverMs = clock.Elapsed.TotalMilliseconds;
    response.Headers["X-Server-Ms"] = serverMs.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    return Results.Json(new
    {
        valid = result.IsValid,
        serverMs,
        diagnostics = result.Diagnostics.Select(d => new { code = d.Code, severity = d.Severity.ToString(), message = d.Message, path = d.Path, nodeId = d.NodeId }),
    });
});

// Execution: start in-process, return an id; events are streamed separately.
app.MapPost("/api/executions", async (HttpRequest request, ExecutionHub hub, WorkflowLoader loader, IWorkflowRunner runner, IIdGenerator ids) =>
{
    using var reader = new StreamReader(request.Body);
    var body = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
    var loaded = loader.Load(body["workflow"]!.ToJsonString());
    if (!loaded.IsValid)
    {
        return Results.BadRequest(new { error = "invalid workflow", diagnostics = loaded.Diagnostics.Select(d => d.ToString()) });
    }

    var record = hub.Start(ids.NewCorrelationId());
    var timeout = body["timeoutMs"]?.GetValue<int>() is { } ms ? TimeSpan.FromMilliseconds(ms) : (TimeSpan?)null;
    _ = Task.Run(() => hub.RunAsync(record, runner, loaded.Workflow, timeout));
    return Results.Accepted($"/api/executions/{record.Id}", new { id = record.Id });
});

app.MapPost("/api/executions/{id}/cancel", (string id, ExecutionHub hub) => hub.Cancel(id) ? Results.Accepted() : Results.NotFound());

// One stream per execution, with Last-Event-ID replay.
app.MapGet("/api/executions/{id}/events", (string id, ExecutionHub hub, HttpRequest request, CancellationToken aborted) =>
{
    if (!hub.TryGet(id, out var record))
    {
        return Results.NotFound();
    }

    var after = long.TryParse(request.Headers["Last-Event-ID"], out var last) ? last : 0;
    return TypedResults.ServerSentEvents(hub.Stream([record], after, aborted));
});

// One multiplexed stream for many executions (avoids the browser's six-connections-per-origin limit on HTTP/1.1).
app.MapGet("/api/events", (string ids, ExecutionHub hub, CancellationToken aborted) =>
{
    var records = ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(i => hub.TryGet(i, out var r) ? r : null).OfType<ExecutionRecord>().ToList();
    return TypedResults.ServerSentEvents(hub.Stream(records, 0, aborted));
});

// Holds a request open (to show connection-limit effects of many open SSE streams).
app.MapGet("/api/ping", () => Results.Text("pong"));

// Span-listener overhead: M concurrent runs of an N-node workflow, with no listener, one listener per run
// (the Phase 5 RunMonitor pattern), or one shared listener dispatching by correlation id.
app.MapPost("/api/bench/listeners", async (int runs, int nodes, string mode, WorkflowLoader loader, IWorkflowRunner runner, IIdGenerator ids) =>
{
    var children = string.Join(",", Enumerable.Range(0, nodes).Select(i => $$"""{ "id": "a{{i}}", "type": "Core.Assign", "properties": { "to": "x", "value": "x + 1" } }"""));
    var workflow = loader.Load($$"""{ "schemaVersion": "1.0", "id": "bench", "name": "bench", "version": "1.0.0", "variables": [ { "name": "x", "type": "Int", "default": 0 } ], "root": { "id": "main", "type": "Core.Sequence", "children": [ {{children}} ] } }""").Workflow!;
    var correlations = Enumerable.Range(0, runs).Select(_ => ids.NewCorrelationId()).ToList();
    var counted = new ConcurrentDictionary<string, int>();
    var listeners = new List<ActivityListener>();
    void Count(Activity a)
    {
        if (a.GetTagItem(DiagnosticNames.CorrelationIdKey)?.ToString() is { } c)
        {
            counted.AddOrUpdate(c, 1, (_, n) => n + 1);
        }
    }

    if (mode == "per-run")
    {
        foreach (var correlation in correlations)
        {
            var mine = correlation.Value;
            var listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == DiagnosticNames.RuntimeActivitySource,
                Sample = (ref ActivityCreationOptions<ActivityContext> o) =>
                    o.Tags is not null && o.Tags.Any(t => t.Key == DiagnosticNames.CorrelationIdKey && Equals(t.Value?.ToString(), mine)) ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
                ActivityStopped = a => { if (a.GetTagItem(DiagnosticNames.CorrelationIdKey)?.ToString() == mine) { Count(a); } },
            };
            ActivitySource.AddActivityListener(listener);
            listeners.Add(listener);
        }
    }
    else if (mode == "shared")
    {
        var wanted = correlations.Select(c => c.Value).ToHashSet();
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == DiagnosticNames.RuntimeActivitySource,
            Sample = (ref ActivityCreationOptions<ActivityContext> o) => ActivitySamplingResult.AllData,
            ActivityStopped = a => { if (a.GetTagItem(DiagnosticNames.CorrelationIdKey)?.ToString() is { } c && wanted.Contains(c)) { Count(a); } },
        };
        ActivitySource.AddActivityListener(listener);
        listeners.Add(listener);
    }

    var clock = Stopwatch.StartNew();
    var results = await Task.WhenAll(correlations.Select(c => Task.Run(() => runner.RunAsync(workflow, new WorkflowRunRequest { CorrelationId = c }))));
    var elapsed = clock.Elapsed.TotalMilliseconds;
    listeners.ForEach(l => l.Dispose());
    return Results.Json(new
    {
        mode,
        runs,
        nodes,
        totalMs = Math.Round(elapsed, 1),
        microsecondsPerNode = Math.Round(elapsed * 1000 / (runs * (double)nodes), 2),
        allSucceeded = results.All(r => r.Succeeded),
        spansObserved = counted.Values.Sum(),
    });
});

app.Run();

internal sealed record ExecutionEvent(long Seq, string Type, string ExecutionId, DateTimeOffset Time, string? NodeId = null, string? Level = null, string? Message = null, string? Status = null, JsonNode? Data = null);

internal sealed class ExecutionRecord(string id, string correlationId)
{
    private readonly List<ExecutionEvent> _events = [];
    private readonly Lock _gate = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Id { get; } = id;

    public string CorrelationId { get; } = correlationId;

    public CancellationTokenSource Cancellation { get; } = new();

    public bool Completed { get; private set; }

    public void Append(string type, string? nodeId = null, string? level = null, string? message = null, string? status = null, JsonNode? data = null, bool final = false)
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            _events.Add(new ExecutionEvent(_events.Count + 1, type, Id, DateTimeOffset.UtcNow, nodeId, level, message, status, data));
            Completed |= final;
            previous = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        previous.TrySetResult();
    }

    public (IReadOnlyList<ExecutionEvent> Events, Task Changed, bool Completed) Since(long seq)
    {
        lock (_gate)
        {
            return ([.. _events.Skip((int)seq)], _changed.Task, Completed);
        }
    }
}

internal sealed class ExecutionHub
{
    private readonly ConcurrentDictionary<string, ExecutionRecord> _byId = new();
    private readonly ConcurrentDictionary<string, ExecutionRecord> _byCorrelation = new();
    private readonly ActivityListener _listener;

    public ExecutionHub()
    {
        // One shared listener that dispatches by correlation id (the better span-based design; see the bench).
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == DiagnosticNames.RuntimeActivitySource,
            Sample = (ref ActivityCreationOptions<ActivityContext> o) => ActivitySamplingResult.AllData,
            ActivityStarted = a => Node(a, started: true),
            ActivityStopped = a => Node(a, started: false),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public ExecutionRecord Start(CorrelationId correlation)
    {
        var record = new ExecutionRecord(correlation.Value, correlation.Value);
        _byId[record.Id] = record;
        _byCorrelation[correlation.Value] = record;
        return record;
    }

    public bool TryGet(string id, out ExecutionRecord record) => _byId.TryGetValue(id, out record!);

    public bool TryGetByCorrelation(string correlation, out ExecutionRecord record) => _byCorrelation.TryGetValue(correlation, out record!);

    public bool Cancel(string id)
    {
        if (!_byId.TryGetValue(id, out var record))
        {
            return false;
        }

        record.Cancellation.Cancel();
        return true;
    }

    public async Task RunAsync(ExecutionRecord record, IWorkflowRunner runner, MyRPA.Workflow.WorkflowDefinition workflow, TimeSpan? timeout)
    {
        record.Append("execution.started");
        var result = await runner.RunAsync(workflow, new WorkflowRunRequest { CorrelationId = new CorrelationId(record.CorrelationId), Timeout = timeout }, record.Cancellation.Token);
        record.Append("execution.completed", nodeId: result.Error?.NodeId, status: result.Status.ToString(), message: result.Error?.Message,
            data: JsonSerializer.SerializeToNode(result.Outputs), final: true);
    }

    public async IAsyncEnumerable<SseItem<ExecutionEvent>> Stream(IReadOnlyList<ExecutionRecord> records, long after, [EnumeratorCancellation] CancellationToken aborted)
    {
        var positions = records.ToDictionary(r => r.Id, _ => records.Count == 1 ? after : 0L);
        while (!aborted.IsCancellationRequested)
        {
            var waits = new List<Task>();
            var open = 0;
            foreach (var record in records)
            {
                var (events, changed, completed) = record.Since(positions[record.Id]);
                foreach (var e in events)
                {
                    positions[record.Id] = e.Seq;
                    yield return new SseItem<ExecutionEvent>(e, e.Type) { EventId = records.Count == 1 ? e.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture) : null };
                }

                if (!completed)
                {
                    open++;
                    waits.Add(changed);
                }
            }

            if (open == 0)
            {
                yield break;
            }

            try
            {
                await Task.WhenAny(waits).WaitAsync(TimeSpan.FromSeconds(15), aborted);
            }
            catch (TimeoutException)
            {
                // Keep-alive would be sent here in production.
            }
        }
    }

    private void Node(Activity activity, bool started)
    {
        if (activity.OperationName == DiagnosticNames.WorkflowExecuteOperation
            || activity.GetTagItem(DiagnosticNames.CorrelationIdKey)?.ToString() is not { } correlation
            || !_byCorrelation.TryGetValue(correlation, out var record)
            || activity.GetTagItem(DiagnosticNames.NodeIdKey)?.ToString() is not { } nodeId)
        {
            return;
        }

        if (started)
        {
            record.Append("node.started", nodeId);
        }
        else
        {
            var outcome = activity.GetTagItem(DiagnosticNames.OutcomeKey)?.ToString();
            record.Append(outcome == "Failed" ? "node.failed" : "node.completed", nodeId, status: outcome);
        }
    }
}

/// <summary>Routes log entries to the execution named by the correlation id in the engine's logging scope.</summary>
internal sealed class ExecutionLogRouter(ExecutionHub hub) : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ILogger CreateLogger(string categoryName) => new Router(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class Router(ExecutionLogRouter owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => category == DiagnosticNames.WorkflowLogCategory && logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string? correlation = null, node = null;
            owner._scopes.ForEachScope(
                (scope, _) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    {
                        foreach (var pair in pairs)
                        {
                            if (pair.Key == DiagnosticNames.CorrelationIdKey) { correlation = pair.Value?.ToString(); }
                            if (pair.Key == DiagnosticNames.NodeIdKey) { node = pair.Value?.ToString(); }
                        }
                    }
                },
                (object?)null);

            if (correlation is not null && owner.Hub.TryGetByCorrelation(correlation, out var record))
            {
                record.Append("log", node, logLevel.ToString(), formatter(state, exception));
            }
        }
    }

    private ExecutionHub Hub => hub;
}
