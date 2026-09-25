using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MyRPA.Activities;
using MyRPA.Contracts.Execution;
using MyRPA.Runtime;
using MyRPA.Workflow;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Validation;

namespace MyRPA.Execution.Hosting.Tests;

/// <summary>A host as a server would compose it: real engine, built-in activities, fake clock, in-memory sub-workflows.</summary>
public sealed class HostingHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    public HostingHarness(Action<ExecutionHostOptions>? configure = null, Dictionary<string, string>? workflows = null)
    {
        _services = new ServiceCollection()
            .AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Trace))
            .AddSingleton<TimeProvider>(Time)
            .AddMyRpaRuntime()
            .AddMyRpaActivities()
            .AddScoped<IWorkflowResolver>(sp => new InMemoryResolver(sp.GetRequiredService<WorkflowLoader>(), workflows ?? []))
            .AddMyRpaExecutionHosting(configure)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));

    public ExecutionHost Host => _services.GetRequiredService<ExecutionHost>();

    public static string Workflow(string root, string arguments = "[]", string id = "test") =>
        $$"""{ "schemaVersion": "1.0", "id": "{{id}}", "name": "Test", "version": "1.0.0", "arguments": {{arguments}}, "root": {{root}} }""";

    public static string Logs(int count, string id = "logs") => Workflow(
        $$"""{ "id": "main", "type": "Core.Sequence", "children": [ {{string.Join(", ", Enumerable.Range(0, count).Select(i => $$"""{ "id": "l{{i}}", "type": "Core.Log", "properties": { "message": "'line {{i}}'" } }"""))}} ] }""",
        id: id);

    public static string Delay(int milliseconds) => Workflow($$"""{ "id": "wait", "type": "Core.Delay", "properties": { "milliseconds": {{milliseconds}} } }""");

    public WorkflowDefinition Load(string json)
    {
        var result = _services.GetRequiredService<WorkflowLoader>().Load(json);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Diagnostics));
        return result.Workflow;
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    private sealed class InMemoryResolver(WorkflowLoader loader, Dictionary<string, string> workflows) : IWorkflowResolver
    {
        public ValueTask<WorkflowResolution> ResolveAsync(string reference, string invokingLocation, string rootLocation, CancellationToken cancellationToken)
        {
            if (!workflows.TryGetValue(reference, out var json))
            {
                return ValueTask.FromResult(WorkflowResolution.Failure($"'{reference}' not found."));
            }

            var result = loader.Load(json);
            return ValueTask.FromResult(result.IsValid ? WorkflowResolution.Success(result.Workflow, reference) : WorkflowResolution.Failure("invalid", reference, result.Diagnostics));
        }
    }
}

public static class EventReading
{
    /// <summary>Reads the whole stream (the run must complete).</summary>
    public static async Task<List<ExecutionEventMessage>> ReadAllAsync(this ExecutionHandle handle, long after = 0)
    {
        var events = new List<ExecutionEventMessage>();
        await foreach (var e in handle.ReadEventsAsync(after, TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        return events;
    }

    /// <summary>Reads until an event matches.</summary>
    public static async Task<ExecutionEventMessage> ReadUntilAsync(this IAsyncEnumerator<ExecutionEventMessage> events, Func<ExecutionEventMessage, bool> match)
    {
        while (await events.MoveNextAsync())
        {
            if (match(events.Current))
            {
                return events.Current;
            }
        }

        throw new InvalidOperationException("The stream ended before a matching event.");
    }

    public static string Describe(ExecutionEventMessage e) => e.Kind switch
    {
        ExecutionEventKinds.NodeStarted => $"node.started {e.NodeId}",
        ExecutionEventKinds.NodeCompleted => $"node.completed {e.NodeId} {e.Status}",
        ExecutionEventKinds.Log => $"log {e.NodeId}: {e.Message}",
        ExecutionEventKinds.ExecutionCompleted => $"execution.completed {e.Status}",
        _ => e.Kind,
    };
}
