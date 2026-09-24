using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyRPA.Activities;
using MyRPA.Core.Activities;
using MyRPA.Runtime;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Validation;

namespace MyRPA.Sdk.Tests;

/// <summary>Records what probe activities observed. Registered as a singleton by the harness.</summary>
public sealed class Probe
{
    private int _alive;

    public ConcurrentQueue<string> Events { get; } = new();

    public ConcurrentQueue<WeakReference> Instances { get; } = new();

    public int MaxAlive { get; private set; }

    public DateTimeOffset? Deadline { get; set; }

    public string? NodeId { get; set; }

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Created(object instance)
    {
        Instances.Enqueue(new WeakReference(instance));
        var alive = Interlocked.Increment(ref _alive);
        MaxAlive = Math.Max(MaxAlive, alive);
        Events.Enqueue("created");
    }

    public void Disposed(string how)
    {
        Interlocked.Decrement(ref _alive);
        Events.Enqueue(how);
    }
}

/// <summary>Real runtime + built-in activities + the given test activities.</summary>
public sealed class SdkHarness : IDisposable
{
    private readonly ServiceProvider _services;

    public SdkHarness(Action<IServiceCollection> activities, TimeProvider? time = null, ILoggerProvider? logs = null)
    {
        var services = new ServiceCollection().AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            if (logs is not null)
            {
                b.AddProvider(logs);
            }
        });
        if (time is not null)
        {
            services.AddSingleton(time);
        }

        services.AddSingleton<Probe>().AddMyRpaRuntime().AddMyRpaActivities();
        activities(services);
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public Probe Probe => _services.GetRequiredService<Probe>();

    public static string Workflow(string root, string arguments = "[]", string variables = "[]") =>
        $$"""{ "schemaVersion": "1.0", "id": "wf", "name": "Test", "version": "1.0.0", "arguments": {{arguments}}, "variables": {{variables}}, "root": {{root}} }""";

    public Task<WorkflowExecutionResult> RunAsync(string json, TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
    {
        var load = _services.GetRequiredService<WorkflowLoader>().Load(json);
        Assert.True(load.IsValid, string.Join(Environment.NewLine, load.Diagnostics));
        return _services.GetRequiredService<IWorkflowRunner>().RunAsync(
            load.Workflow!, new WorkflowRunRequest { Timeout = timeout }, cancellationToken ?? TestContext.Current.CancellationToken);
    }

    public void Dispose() => _services.Dispose();

    public static ActivityDescriptor Descriptor(string name, params ActivityPropertyDefinition[] properties) =>
        new(new ActivityTypeName(name), name, "Test", properties: properties);
}

/// <summary>Captures log entries with their scope values.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ConcurrentQueue<(string Category, string Message, IReadOnlyDictionary<string, object?> Scope)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
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
            owner.Entries.Enqueue((category, formatter(state, exception), values));
        }
    }
}
