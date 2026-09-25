using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MyRPA.Core.Diagnostics;

namespace MyRPA.Studio.Running;

/// <summary>A log entry shown in the Logs pane.</summary>
/// <param name="Time">When it was written.</param>
/// <param name="Level">Severity.</param>
/// <param name="Category">Logger category (e.g. <c>MyRPA.Workflow.Log</c> for Core.Log).</param>
/// <param name="Message">Rendered message.</param>
/// <param name="NodeId">The node that was executing, from the engine's logging scope.</param>
public sealed record StudioLogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message, string? NodeId);

/// <summary>
/// Receives log entries from any thread and hands them to whoever displays them (the Logs pane view model). Registered
/// as a logging provider by the Studio host, so workflow <c>Core.Log</c> messages and plugin logs appear in Studio with
/// the node that wrote them.
/// </summary>
/// <param name="timeProvider">Timestamps the entries.</param>
public sealed class StudioLogFeed(TimeProvider timeProvider) : ILoggerProvider, ISupportExternalScope
{
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    /// <summary>Raised on the writing thread for every entry at or above <see cref="MinimumLevel"/>.</summary>
    public event EventHandler<StudioLogEntry>? EntryWritten;

    /// <summary>Lowest level forwarded (Information by default).</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FeedLogger(this, categoryName);

    /// <inheritdoc />
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class FeedLogger(StudioLogFeed feed, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => feed._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= feed.MinimumLevel && logLevel != LogLevel.None
            && !category.StartsWith("Microsoft.", StringComparison.Ordinal) && !category.StartsWith("System.", StringComparison.Ordinal);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string? nodeId = null;
            feed._scopes.ForEachScope(
                (scope, _) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    {
                        foreach (var pair in pairs)
                        {
                            if (pair.Key == DiagnosticNames.NodeIdKey && pair.Value is not null)
                            {
                                nodeId = pair.Value.ToString();
                            }
                        }
                    }
                },
                (object?)null);

            feed.EntryWritten?.Invoke(feed, new StudioLogEntry(feed._timeProvider.GetUtcNow(), logLevel, category, formatter(state, exception), nodeId));
        }
    }
}

/// <summary>
/// Follows one run through the engine's tracing spans (no engine changes): reports which node starts and stops, so the
/// designer can highlight the running node. Only spans tagged with the run's correlation id are recorded.
/// </summary>
public sealed class RunMonitor : IDisposable
{
    private readonly string _correlationId;
    private readonly ActivityListener _listener;
    private readonly ConcurrentDictionary<string, byte> _started = new(StringComparer.Ordinal);

    /// <summary>Starts listening.</summary>
    /// <param name="correlationId">The run's correlation id (set on the run request).</param>
    public RunMonitor(string correlationId)
    {
        _correlationId = correlationId ?? throw new ArgumentNullException(nameof(correlationId));
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DiagnosticNames.RuntimeActivitySource,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => IsOurs(options.Tags) ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            ActivityStarted = activity => Report(activity, started: true),
            ActivityStopped = activity => Report(activity, started: false),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>A node started executing (raised on the engine's thread).</summary>
    public event EventHandler<string>? NodeStarted;

    /// <summary>A node finished executing (raised on the engine's thread).</summary>
    public event EventHandler<string>? NodeStopped;

    /// <summary>Node ids that started during the run.</summary>
    public IReadOnlyCollection<string> StartedNodes => [.. _started.Keys];

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();

    private bool IsOurs(IEnumerable<KeyValuePair<string, object?>>? tags) =>
        tags is not null && tags.Any(t => t.Key == DiagnosticNames.CorrelationIdKey && string.Equals(t.Value?.ToString(), _correlationId, StringComparison.Ordinal));

    private void Report(Activity activity, bool started)
    {
        if (activity.GetTagItem(DiagnosticNames.CorrelationIdKey)?.ToString() != _correlationId
            || activity.GetTagItem(DiagnosticNames.NodeIdKey)?.ToString() is not { } nodeId)
        {
            return;
        }

        if (started)
        {
            _started.TryAdd(nodeId, 0);
            NodeStarted?.Invoke(this, nodeId);
        }
        else
        {
            NodeStopped?.Invoke(this, nodeId);
        }
    }
}
