using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MyRPA.Core.Diagnostics;
using MyRPA.Core.Identifiers;
using MyRPA.Workflow.Execution;

namespace MyRPA.Execution.Hosting;

/// <summary>The host's runs by run id (the run's correlation id). Shared by the host and the log router.</summary>
internal sealed class ExecutionRegistry
{
    private readonly ConcurrentDictionary<string, ExecutionRecord> _runs = new(StringComparer.Ordinal);

    public void Add(ExecutionRecord record)
    {
        if (!_runs.TryAdd(record.RunId, record))
        {
            throw new InvalidOperationException($"A run with id '{record.RunId}' already exists.");
        }
    }

    public bool TryGet(string runId, [NotNullWhen(true)] out ExecutionRecord? record) => _runs.TryGetValue(runId, out record);

    public bool Remove(string runId, [NotNullWhen(true)] out ExecutionRecord? record) => _runs.TryRemove(runId, out record);
}

/// <summary>
/// Routes log entries written during a run to that run's event stream. The run is found through the correlation id in
/// the engine's logging scope (ADR-0006), so entries of one run can never reach another. Routed: workflow logs
/// (<see cref="DiagnosticNames.WorkflowLogCategory"/>, Information and above) and warnings or errors from other MyRPA
/// and plugin categories. Framework categories (<c>Microsoft.*</c>, <c>System.*</c>) are not routed.
/// </summary>
internal sealed class ExecutionLogRouter(ExecutionRegistry registry, TimeProvider time) : ILoggerProvider, ISupportExternalScope
{
    private readonly ExecutionRegistry _registry = registry;
    private readonly TimeProvider _time = time;
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ILogger CreateLogger(string categoryName) => new Router(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class Router(ExecutionLogRouter owner, string category) : ILogger
    {
        private readonly bool _workflowLog = category == DiagnosticNames.WorkflowLogCategory;
        private readonly bool _framework = category.StartsWith("Microsoft.", StringComparison.Ordinal) || category.StartsWith("System.", StringComparison.Ordinal);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && (_workflowLog ? logLevel >= LogLevel.Information : !_framework && logLevel >= LogLevel.Warning);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // Innermost values win: a node scope is nested inside its execution scope.
            string? correlation = null, execution = null, node = null;
            owner._scopes.ForEachScope(
                (scope, _) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    {
                        foreach (var pair in pairs)
                        {
                            switch (pair.Key)
                            {
                                case DiagnosticNames.CorrelationIdKey:
                                    correlation = pair.Value?.ToString();
                                    break;
                                case DiagnosticNames.ExecutionIdKey:
                                    execution = pair.Value?.ToString();
                                    break;
                                case DiagnosticNames.NodeIdKey:
                                    node = pair.Value?.ToString();
                                    break;
                            }
                        }
                    }
                },
                (object?)null);

            if (correlation is not null && owner._registry.TryGet(correlation, out var record))
            {
                record.AppendLog(owner._time.GetUtcNow(), logLevel.ToString(), formatter(state, exception), execution, node);
            }
        }
    }
}

/// <summary>Registration of execution hosting.</summary>
public static class ExecutionHostingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ExecutionHost"/> and the per-run log router. The host composes the engine itself
    /// (<c>AddMyRpaRuntime</c>, activities, storage) and logging (<c>AddLogging</c>).
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="configure">Adjusts <see cref="ExecutionHostOptions"/>.</param>
    public static IServiceCollection AddMyRpaExecutionHosting(this IServiceCollection services, Action<ExecutionHostOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new ExecutionHostOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton<ExecutionRegistry>();
        services.TryAddSingleton(sp => new ExecutionHost(
            sp.GetRequiredService<IWorkflowRunner>(),
            sp.GetRequiredService<IIdGenerator>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ExecutionHostOptions>(),
            sp.GetRequiredService<ExecutionRegistry>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, ExecutionLogRouter>(
            sp => new ExecutionLogRouter(sp.GetRequiredService<ExecutionRegistry>(), sp.GetRequiredService<TimeProvider>())));
        return services;
    }
}
