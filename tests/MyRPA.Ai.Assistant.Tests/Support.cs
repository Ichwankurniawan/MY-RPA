using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MyRPA.Activities;
using MyRPA.Ai.Contracts;
using MyRPA.Core.Activities;

namespace MyRPA.Ai.Assistant.Tests;

/// <summary>A deterministic provider: answers in order (the last one repeats) and records every request.</summary>
public sealed class FakeProvider : IAiProvider
{
    private readonly List<Func<CancellationToken, Task<AiCompletion>>> _answers = [];

    public List<AiRequest> Requests { get; } = [];

    public string? Unavailable { get; set; }

    public AiProviderInfo Info => new("fake", "fake-model", true) { UnavailableReason = Unavailable };

    public FakeProvider Answer(string text)
    {
        _answers.Add(_ => Task.FromResult(new AiCompletion(text, "fake-model") { Usage = new AiUsage(10, 5) }));
        return this;
    }

    public FakeProvider Fail(AiFailureKind kind)
    {
        _answers.Add(_ => throw new AiProviderException(kind, $"fake {kind}"));
        return this;
    }

    public FakeProvider WaitForCancellation()
    {
        _answers.Add(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("unreachable");
        });
        return this;
    }

    public Task<AiCompletion> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return _answers[Math.Min(Requests.Count - 1, _answers.Count - 1)](cancellationToken);
    }
}

/// <summary>Keeps formatted log messages, to prove what is (not) logged.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}

/// <summary>The built-in catalog plus a code activity (to prove it is excluded) and a file-writing activity (side effects).</summary>
public static class TestCatalog
{
    private static readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddMyRpaActivities().BuildServiceProvider();

    public static IActivityCatalog Instance { get; } = new Combined(
        _services.GetRequiredService<IActivityCatalog>(),
        [
            new ActivityDescriptor(new ActivityTypeName("Code.JavaScript"), "JavaScript", "Code", "Runs JavaScript.", [new ActivityPropertyDefinition("code", ActivityPropertyKind.Text, isRequired: true)]),
            new ActivityDescriptor(new ActivityTypeName("Test.WriteFile"), "Write file", "Files", "Writes text to a file.", [new ActivityPropertyDefinition("path", ActivityPropertyKind.Expression, isRequired: true)]) { SideEffects = ActivitySideEffects.FileSystem },
        ]);

    public static WorkflowAssistant Assistant(FakeProvider provider, AssistantOptions? options = null, ILogger<WorkflowAssistant>? logger = null) =>
        new(provider, Instance, logger ?? new ListLogger<WorkflowAssistant>(), new FakeTimeProvider(), options);

    private sealed class Combined(IActivityCatalog inner, IReadOnlyList<ActivityDescriptor> extra) : IActivityCatalog
    {
        public IReadOnlyList<ActivityDescriptor> Descriptors { get; } = [.. inner.Descriptors, .. extra];

        public bool TryGet(ActivityTypeName typeName, [NotNullWhen(true)] out ActivityDescriptor? descriptor)
        {
            descriptor = extra.FirstOrDefault(d => d.TypeName == typeName);
            return descriptor is not null || inner.TryGet(typeName, out descriptor);
        }
    }
}

/// <summary>Workflows and model answers.</summary>
public static class Samples
{
    public const string Valid = """
        { "schemaVersion": "1.0", "id": "greet", "name": "Greet", "version": "1.0.0",
          "arguments": [ { "name": "who", "direction": "In", "type": "String", "default": "World" } ],
          "root": { "id": "main", "type": "Core.Sequence", "children": [
            { "id": "say", "type": "Core.Log", "properties": { "message": "'Hello ' + who" } } ] } }
        """;

    public static string WorkflowWith(string children, string id = "greet", string schema = "1.0") => $$"""
        { "schemaVersion": "{{schema}}", "id": "{{id}}", "name": "Greet", "version": "1.0.0",
          "root": { "id": "main", "type": "Core.Sequence", "children": [ {{children}} ] } }
        """;

    public static string Proposal(string workflow, params string[] plan) => JsonSerializer.Serialize(new
    {
        kind = "proposal",
        known = new[] { "greets someone" },
        assumptions = new[] { "the name is an input argument" },
        questions = Array.Empty<object>(),
        missing = Array.Empty<string>(),
        plan = plan.Length > 0 ? plan : ["Log a greeting"],
        summary = "Logs a greeting.",
        workflowJson = workflow,
    });

    public static string Clarify(params string[] questions) => JsonSerializer.Serialize(new
    {
        kind = "clarify",
        known = new[] { "invoices are downloaded" },
        assumptions = new[] { "today means the local date" },
        questions = questions.Select(q => new { question = q, options = new[] { "Skip", "Update" } }),
        missing = Array.Empty<string>(),
        plan = Array.Empty<string>(),
        summary = string.Empty,
        workflowJson = string.Empty,
    });

    public static string Unsupported(params string[] missing) => JsonSerializer.Serialize(new
    {
        kind = "unsupported",
        known = Array.Empty<string>(),
        assumptions = Array.Empty<string>(),
        questions = Array.Empty<object>(),
        missing,
        plan = Array.Empty<string>(),
        summary = "No activity can read a scanner.",
        workflowJson = string.Empty,
    });
}
