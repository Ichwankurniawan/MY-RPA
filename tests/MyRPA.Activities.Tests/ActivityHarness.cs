using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MyRPA.Runtime;
using MyRPA.Workflow;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Validation;

namespace MyRPA.Activities.Tests;

/// <summary>Real engine + built-in activities, fake clock, captured logs, in-memory workflow resolver.</summary>
public sealed class ActivityHarness : IDisposable
{
    private readonly ServiceProvider _services;

    public ActivityHarness(Dictionary<string, string>? workflows = null)
    {
        _services = new ServiceCollection()
            .AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(Logs))
            .AddSingleton<TimeProvider>(Time)
            .AddMyRpaRuntime()
            .AddMyRpaActivities()
            .AddScoped<IWorkflowResolver>(sp => new InMemoryResolver(sp.GetRequiredService<WorkflowLoader>(), workflows ?? []))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero));

    public LogCollector Logs { get; } = new();

    public IServiceProvider Services => _services;

    public static string Workflow(string root, string arguments = "[]", string variables = "[]", string id = "test") =>
        $$"""{ "schemaVersion": "1.0", "id": "{{id}}", "name": "Test", "version": "1.0.0", "arguments": {{arguments}}, "variables": {{variables}}, "root": {{root}} }""";

    public WorkflowDefinition Load(string json)
    {
        var result = _services.GetRequiredService<WorkflowLoader>().Load(json);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Diagnostics));
        return result.Workflow;
    }

    public Task<WorkflowExecutionResult> RunAsync(string json, IReadOnlyDictionary<string, object?>? arguments = null, CancellationToken? cancellationToken = null) =>
        _services.GetRequiredService<IWorkflowRunner>().RunAsync(
            Load(json),
            new WorkflowRunRequest { Arguments = arguments ?? new Dictionary<string, object?>(), Location = "main" },
            cancellationToken ?? TestContext.Current.CancellationToken);

    public void Dispose() => _services.Dispose();

    private sealed class InMemoryResolver(WorkflowLoader loader, Dictionary<string, string> workflows) : IWorkflowResolver
    {
        public ValueTask<WorkflowResolution> ResolveAsync(string reference, string invokingLocation, string rootLocation, CancellationToken cancellationToken)
        {
            if (!workflows.TryGetValue(reference, out var json))
            {
                return ValueTask.FromResult(WorkflowResolution.Failure($"'{reference}' not found."));
            }

            var result = loader.Load(json);
            return ValueTask.FromResult(result.IsValid
                ? WorkflowResolution.Success(result.Workflow, reference)
                : WorkflowResolution.Failure("invalid", reference, result.Diagnostics));
        }
    }
}

public sealed class LogCollector : ILoggerProvider
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(LogCollector owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue((category, logLevel, formatter(state, exception)));
    }
}
