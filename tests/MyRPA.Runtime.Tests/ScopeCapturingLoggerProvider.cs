using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MyRPA.Runtime.Tests;

/// <summary>Test logger provider that records each log entry together with the active scope values.</summary>
public sealed class ScopeCapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ConcurrentQueue<CapturedEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    public sealed record CapturedEntry(string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> ScopeValues);

    private sealed class CapturingLogger(ScopeCapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = new Dictionary<string, object?>();
            owner._scopes.ForEachScope(
                (scope, acc) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    {
                        foreach (var pair in pairs)
                        {
                            acc[pair.Key] = pair.Value;
                        }
                    }
                },
                values);
            owner.Entries.Enqueue(new CapturedEntry(category, logLevel, formatter(state, exception), values));
        }
    }
}
