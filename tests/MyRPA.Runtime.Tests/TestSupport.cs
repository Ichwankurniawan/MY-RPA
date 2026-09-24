using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MyRPA.Core.Activities;
using MyRPA.Core.Diagnostics;
using MyRPA.Core.Identifiers;
using MyRPA.Workflow;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Validation;

namespace MyRPA.Runtime.Tests;

/// <summary>Runs workflows with the xUnit test cancellation token unless a specific token is given.</summary>
public static class RunnerTestExtensions
{
    public static Task<WorkflowExecutionResult> RunTestAsync(
        this IWorkflowRunner runner,
        WorkflowDefinition workflow,
        WorkflowRunRequest request,
        CancellationToken? cancellationToken = null) =>
        runner.RunAsync(workflow, request, cancellationToken ?? TestContext.Current.CancellationToken);
}

/// <summary>Deterministic ids: executions 000...001, 000...002; correlations corr-1, corr-2.</summary>
public sealed class SequentialIdGenerator : IIdGenerator
{
    private int _execution;
    private int _correlation;

    public ExecutionId NewExecutionId() => new(new Guid(Interlocked.Increment(ref _execution), 0, 0, new byte[8]));

    public CorrelationId NewCorrelationId() =>
        new("corr-" + Interlocked.Increment(ref _correlation).ToString(CultureInfo.InvariantCulture));
}

/// <summary>A scoped service used to prove one DI scope per top-level execution.</summary>
public sealed class RunProbe : IDisposable
{
    public Guid Instance { get; } = Guid.NewGuid();

    public bool Disposed { get; private set; }

    public void Dispose() => Disposed = true;
}

/// <summary>Records which <see cref="RunProbe"/> instances activities saw.</summary>
public sealed class ProbeLog
{
    public ConcurrentQueue<RunProbe> Seen { get; } = new();
}

/// <summary>In-memory resolver for InvokeWorkflow tests (location = the reference text).</summary>
public sealed class InMemoryResolver(Dictionary<string, WorkflowDefinition> workflows) : IWorkflowResolver
{
    public ValueTask<WorkflowResolution> ResolveAsync(string reference, string invokingLocation, string rootLocation, CancellationToken cancellationToken) =>
        ValueTask.FromResult(workflows.TryGetValue(reference, out var workflow)
            ? WorkflowResolution.Success(workflow, reference)
            : WorkflowResolution.Failure($"'{reference}' not found."));
}

/// <summary>Test-only activities; the engine must work without the built-in library.</summary>
public sealed class TestActivities : IActivityCatalog, IActivityFactory
{
    private readonly Dictionary<ActivityTypeName, ActivityDescriptor> _descriptors;

    public TestActivities()
    {
        ActivityDescriptor[] all =
        [
            new(new("Test.Sequence"), "Sequence", "Test", allowsChildren: true),
            new(new("Test.Set"), "Set", "Test", properties:
            [
                new("to", ActivityPropertyKind.AssignmentTarget, isRequired: true),
                new("value", ActivityPropertyKind.Expression, isRequired: true),
            ]),
            new(new("Test.Fail"), "Fail", "Test", properties: [new("message", ActivityPropertyKind.Expression, isRequired: true)]),
            new(new("Test.Wait"), "Wait", "Test", properties: [new("milliseconds", ActivityPropertyKind.Expression, isRequired: true)]),
            new(new("Test.Probe"), "Probe", "Test"),
            new(new("Test.Log"), "Log", "Test", properties: [new("message", ActivityPropertyKind.Expression, isRequired: true)]),
            new(new("Test.Invoke"), "Invoke", "Test", properties:
            [
                new("workflow", ActivityPropertyKind.Text, isRequired: true),
                new("arguments", ActivityPropertyKind.ExpressionMap),
                new("outputs", ActivityPropertyKind.AssignmentTargetMap),
                new("timeoutMilliseconds", ActivityPropertyKind.Expression),
            ]),
        ];
        _descriptors = all.ToDictionary(d => d.TypeName);
        Descriptors = all;
    }

    public IReadOnlyList<ActivityDescriptor> Descriptors { get; }

    public bool TryGet(ActivityTypeName typeName, [NotNullWhen(true)] out ActivityDescriptor? descriptor) =>
        _descriptors.TryGetValue(typeName, out descriptor);

    public IActivity Create(ActivityTypeName typeName, IServiceProvider services) => typeName.Value switch
    {
        "Test.Sequence" => new Sequence(),
        "Test.Set" => new Set(),
        "Test.Fail" => new Fail(),
        "Test.Wait" => new Wait(),
        "Test.Probe" => new Probe(services.GetRequiredService<RunProbe>(), services.GetRequiredService<ProbeLog>()),
        "Test.Log" => new Log(services.GetRequiredService<ILoggerFactory>()),
        "Test.Invoke" => new Invoke(),
        _ => throw new InvalidOperationException($"Activity type '{typeName}' is not registered."),
    };

    private sealed class Sequence : IActivity
    {
        public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
        {
            foreach (var child in context.Node.Children)
            {
                await context.ExecuteAsync(child);
            }

            return ActivityResult.Completed;
        }
    }

    private sealed class Set : IActivity
    {
        public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
        {
            context.SetValue(context.GetName("to"), context.Evaluate("value"));
            return ActivityResult.CompletedTask;
        }
    }

    private sealed class Fail : IActivity
    {
        public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context) =>
            throw new InvalidOperationException(context.EvaluateText("message"));
    }

    private sealed class Wait : IActivity
    {
        public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(context.EvaluateInt("milliseconds")), context.TimeProvider, context.CancellationToken);
            return ActivityResult.Completed;
        }
    }

    private sealed class Probe(RunProbe probe, ProbeLog log) : IActivity
    {
        public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
        {
            log.Seen.Enqueue(probe);
            return ActivityResult.CompletedTask;
        }
    }

    private sealed class Log(ILoggerFactory factory) : IActivity
    {
        public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
        {
            factory.CreateLogger("Test.Workflow").LogInformation("{Message}", context.EvaluateText("message"));
            return ActivityResult.CompletedTask;
        }
    }

    private sealed class Invoke : IActivity
    {
        public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
        {
            var arguments = context.HasProperty("arguments") ? context.EvaluateMap("arguments") : new Dictionary<string, object?>();
            TimeSpan? timeout = context.HasProperty("timeoutMilliseconds") ? TimeSpan.FromMilliseconds(context.EvaluateInt("timeoutMilliseconds")) : null;
            var result = await context.InvokeWorkflowAsync(context.GetText("workflow"), arguments, timeout);
            if (!result.Succeeded)
            {
                throw new WorkflowInvocationException(ExecutionErrorCodes.InvokedWorkflowFailed, $"child {result.Status}: {result.Error!.Message}", result);
            }

            if (context.HasProperty("outputs"))
            {
                foreach (var (child, target) in context.GetNameMap("outputs"))
                {
                    context.SetValue(target, result.Outputs[child]);
                }
            }

            return ActivityResult.Completed;
        }
    }
}

/// <summary>Builds a runtime container with fake time, sequential ids, captured logs and test activities.</summary>
public sealed class RuntimeHarness : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _services;

    public RuntimeHarness(
        Dictionary<string, string>? childWorkflows = null,
        Action<WorkflowRuntimeOptions>? configure = null,
        bool registerResolver = true)
    {
        var services = new ServiceCollection()
            .AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(Logs))
            .AddSingleton<TimeProvider>(Time)
            .AddSingleton<IIdGenerator>(Ids)
            .AddSingleton(Activities)
            .AddSingleton<IActivityCatalog>(Activities)
            .AddSingleton<IActivityFactory>(Activities)
            .AddScoped<RunProbe>()
            .AddSingleton<ProbeLog>()
            .AddMyRpaRuntime(configure);

        if (registerResolver)
        {
            services.AddScoped<IWorkflowResolver>(sp => new InMemoryResolver(
                (childWorkflows ?? []).ToDictionary(kv => kv.Key, kv => sp.GetRequiredService<WorkflowLoader>().Load(kv.Value).Workflow!)));
        }

        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public FakeTimeProvider Time { get; } = new(Start);

    public SequentialIdGenerator Ids { get; } = new();

    public TestActivities Activities { get; } = new();

    public ScopeCapturingLoggerProvider Logs { get; } = new();

    public IServiceProvider Services => _services;

    public IWorkflowRunner Runner => _services.GetRequiredService<IWorkflowRunner>();

    public WorkflowDefinition Load(string json)
    {
        var result = _services.GetRequiredService<WorkflowLoader>().Load(json);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Diagnostics));
        return result.Workflow;
    }

    public void Dispose() => _services.Dispose();

    /// <summary>Wraps a root node in a minimal workflow document.</summary>
    public static string Workflow(string root, string arguments = "[]", string variables = "[]", string id = "test") =>
        $$"""{ "schemaVersion": "1.0", "id": "{{id}}", "name": "Test", "version": "1.0.0", "arguments": {{arguments}}, "variables": {{variables}}, "root": {{root}} }""";
}

/// <summary>Collects stopped spans of the runtime source belonging to one correlation id.</summary>
public sealed class SpanCollector : IDisposable
{
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private readonly ActivityListener _listener;

    public SpanCollector(string correlationId)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DiagnosticNames.RuntimeActivitySource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (Equals(a.GetTagItem(DiagnosticNames.CorrelationIdKey), correlationId))
                {
                    _stopped.Enqueue(a);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<Activity> Spans => [.. _stopped];

    public void Dispose() => _listener.Dispose();
}
