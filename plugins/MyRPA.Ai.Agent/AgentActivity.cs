using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyRPA.Ai.Contracts;
using MyRPA.Core.Activities;
using MyRPA.Core.Diagnostics;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;

namespace MyRPA.Ai.Agent;

/// <summary>The error types of <c>AI.Agent</c> (the workflow error's <c>errorType</c>, catchable with TryCatch).</summary>
public static class AgentErrorTypes
{
    /// <summary>A property is missing or wrong (tools, schema, budgets).</summary>
    public const string InvalidInput = "InvalidInput";

    /// <summary>No AI provider is configured or available.</summary>
    public const string NotConfigured = "AgentNotConfigured";

    /// <summary>The AI provider failed (the message names its failure kind).</summary>
    public const string ProviderError = "AgentProviderError";

    /// <summary>The model kept answering in the wrong shape.</summary>
    public const string InvalidResponse = "AgentInvalidResponse";

    /// <summary>The final output kept failing the output schema.</summary>
    public const string InvalidOutput = "AgentInvalidOutput";

    /// <summary>The iteration, tool-call or token budget ran out.</summary>
    public const string BudgetExhausted = "AgentBudgetExhausted";

    /// <summary>The agent ran longer than its timeout.</summary>
    public const string Timeout = "Timeout";

    /// <summary>A configured MCP server could not be reached or listed.</summary>
    public const string ToolSourceUnavailable = "AgentToolSourceUnavailable";
}

/// <summary>
/// <c>AI.Agent</c> (ADR-0048): a bounded AI agent as an ordinary workflow activity. It works on <c>task</c> following
/// <c>instructions</c>, may call only the <c>tools</c> the author listed (checked at every call), within its budgets,
/// and stores output validated against <c>outputSchema</c> in <c>result</c>. Tool calls run through the engine and the
/// plugins' own services; the model never runs code or reaches anything except through those tools.
/// </summary>
public sealed class AgentActivity(IEnumerable<IAiProvider> providers, AgentOptions options, ILoggerFactory loggers) : IActivity
{
    private static readonly EventId _workflowLogEvent = new(4000, "WorkflowLog");
    private readonly ILogger _log = (loggers ?? throw new ArgumentNullException(nameof(loggers))).CreateLogger(DiagnosticNames.WorkflowLogCategory);

    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("AI.Agent"),
        "AI Agent",
        "AI",
        "Works on one task with an AI model, using only the tools you list (project workflows, MCP server tools, built-ins), within iteration, tool-call, time and token budgets. The output is checked against your schema before it is stored. Every tool call and its outcome is written to the execution log.",
        [
            new("instructions", ActivityPropertyKind.Text, isRequired: true, "What the agent is for and how it should work (your rules; the task and tool results cannot change them).") { ValueType = ActivityValueType.String, IsMultiline = true },
            new("task", ActivityPropertyKind.Expression, isRequired: true, "The task input: any value (text, a Dictionary of fields, a List). It is sent to the model as data, never as instructions.") { ValueType = ActivityValueType.Any },
            new("tools", ActivityPropertyKind.Text, isRequired: false, "The tools the agent may use, as a JSON array of { \"workflow\": \"tools/lookup.json\", \"name\": \"lookup\", \"description\": \"...\", \"input\": { \"id\": \"String\" } }, { \"mcp\": \"crm\", \"tool\": \"search\" } or { \"builtin\": \"regex_extract\" }. Nothing else can be called.") { ValueType = ActivityValueType.String, IsMultiline = true, Editor = "agentTools" },
            new("outputSchema", ActivityPropertyKind.Text, isRequired: false, "A JSON schema the final output must meet (type, properties, required, items, enum...). Empty: any JSON value.") { ValueType = ActivityValueType.String, IsMultiline = true },
            new("maxIterations", ActivityPropertyKind.Expression, isRequired: false, "The most model answers the agent may use.") { ValueType = ActivityValueType.Int, DefaultValue = "8" },
            new("maxToolCalls", ActivityPropertyKind.Expression, isRequired: false, "The most tool calls the agent may make.") { ValueType = ActivityValueType.Int, DefaultValue = "10" },
            new("timeoutMs", ActivityPropertyKind.Expression, isRequired: false, "How long the whole agent may run, in milliseconds.") { ValueType = ActivityValueType.Int, DefaultValue = "120000" },
            new("toolTimeoutMs", ActivityPropertyKind.Expression, isRequired: false, "How long one tool call may run, in milliseconds.") { ValueType = ActivityValueType.Int, DefaultValue = "30000" },
            new("maxTokens", ActivityPropertyKind.Expression, isRequired: false, "The most tokens (in and out) the agent may use; enforced when the provider reports usage.") { ValueType = ActivityValueType.Int },
            new("result", ActivityPropertyKind.AssignmentTarget, isRequired: false, "Receives the validated output.") { ValueType = ActivityValueType.Any },
            new("toolCalls", ActivityPropertyKind.AssignmentTarget, isRequired: false, "Receives the tool calls made: a List of { iteration, tool, status, durationMs } (no arguments or results).") { ValueType = ActivityValueType.List },
        ])
    { SideEffects = ActivitySideEffects.Network };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var provider = providers.FirstOrDefault()
            ?? throw new ActivityFailedException(AgentErrorTypes.NotConfigured, $"{context.Node.Type} '{context.Node.Id}' needs an AI provider: add an AI provider plugin (such as MyRPA.Ai.OpenAiCompatible) to the plugin configuration.");
        if (provider.Info.UnavailableReason is { } reason)
        {
            throw new ActivityFailedException(AgentErrorTypes.NotConfigured, $"The AI provider is not available: {reason}");
        }

        Exception Fail(string message) => new ActivityFailedException(AgentErrorTypes.InvalidInput, $"{context.Node.Type} '{context.Node.Id}': {message}");
        var instructions = context.GetText("instructions");
        var task = TaskJson(context);
        var schema = Schema(context, Fail);
        var budget = Budget(context, Fail);
        var specs = ToolSpecs.Parse(context.HasProperty("tools") ? context.GetText("tools") : string.Empty, options, Fail);
        var events = new AgentEvents(_log, context.Node.Id.Value);
        var mcp = new McpSessions(options);
        await using (mcp.ConfigureAwait(false))
        {
            var tools = await ToolSet.BuildAsync(specs, context, mcp, events, Fail).ConfigureAwait(false);
            var runtime = new AgentRuntime(provider, events, context.TimeProvider, options.MaxToolResultChars);
            var run = await runtime.RunAsync(new AgentSpec(instructions, task, tools, schema, budget), context.CancellationToken).ConfigureAwait(false);
            Publish(context, run);
            return ActivityResult.Completed;
        }
    }

    private static void Publish(IActivityContext context, AgentRunResult run)
    {
        if (context.HasProperty("toolCalls"))
        {
            context.SetValue(context.GetName("toolCalls"), WorkflowValues.List(run.Calls.Select(c => (object?)WorkflowValues.Dictionary(new Dictionary<string, object?>
            {
                ["iteration"] = (long)c.Iteration,
                ["tool"] = c.Tool,
                ["status"] = c.Status.ToString(),
                ["durationMs"] = c.DurationMs,
            }))));
        }

        if (run.Outcome == AgentOutcome.Cancelled)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(context.CancellationToken);
        }

        if (run.Outcome != AgentOutcome.Completed)
        {
            throw new ActivityFailedException(ErrorType(run.Outcome), $"{context.Node.Type} '{context.Node.Id}': {run.Message}");
        }

        if (context.HasProperty("result"))
        {
            using var output = JsonDocument.Parse(run.Output?.ToJsonString() ?? "null");
            if (!WorkflowValues.TryFromJsonUntyped(output.RootElement, out var value, out var error))
            {
                throw new ActivityFailedException(AgentErrorTypes.InvalidOutput, $"{context.Node.Type} '{context.Node.Id}': the output cannot become a workflow value: {error}");
            }

            context.SetValue(context.GetName("result"), value);
        }
    }

    private static string ErrorType(AgentOutcome outcome) => outcome switch
    {
        AgentOutcome.ProviderError => AgentErrorTypes.ProviderError,
        AgentOutcome.InvalidResponse => AgentErrorTypes.InvalidResponse,
        AgentOutcome.InvalidOutput => AgentErrorTypes.InvalidOutput,
        AgentOutcome.BudgetExhausted => AgentErrorTypes.BudgetExhausted,
        AgentOutcome.TimedOut => AgentErrorTypes.Timeout,
        _ => AgentErrorTypes.NotConfigured,
    };

    private static string TaskJson(IActivityContext context)
    {
        using var buffer = new MemoryStream();
        // Readable JSON (no >-style escaping): the model reads it, and the data fence can see what it holds.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            WorkflowValues.WriteJson(writer, context.Evaluate("task"));
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static JsonElement? Schema(IActivityContext context, Func<string, Exception> fail)
    {
        var text = context.HasProperty("outputSchema") ? context.GetText("outputSchema").Trim() : string.Empty;
        if (text.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False
                ? document.RootElement.Clone()
                : throw fail("'outputSchema' must be a JSON schema object.");
        }
        catch (JsonException ex)
        {
            throw fail($"'outputSchema' is not JSON: {ex.Message}");
        }
    }

    private AgentBudget Budget(IActivityContext context, Func<string, Exception> fail)
    {
        int Int(string name, int fallback, int max)
        {
            if (!context.HasProperty(name))
            {
                return fallback;
            }

            return context.Evaluate(name) is long n && n >= 0 && n <= max
                ? (int)n
                : throw fail(string.Create(CultureInfo.InvariantCulture, $"'{name}' must be a whole number from 0 to {max}."));
        }

        var timeout = TimeSpan.FromMilliseconds(Math.Max(1, Int("timeoutMs", 120_000, (int)options.MaxTimeout.TotalMilliseconds)));
        if (context.Deadline is { } deadline)
        {
            var left = deadline - context.TimeProvider.GetUtcNow();
            if (left < timeout)
            {
                timeout = left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1);
            }
        }

        return new AgentBudget(
            Math.Max(1, Int("maxIterations", 8, options.MaxIterations)),
            Int("maxToolCalls", 10, options.MaxToolCalls),
            timeout,
            TimeSpan.FromMilliseconds(Math.Max(1, Int("toolTimeoutMs", 30_000, 3_600_000))),
            context.HasProperty("maxTokens") ? Int("maxTokens", 0, int.MaxValue) : null);
    }

    /// <summary>Agent events in the run's execution log (actions and outcomes only; never prompts, answers or payloads).</summary>
    internal sealed class AgentEvents(ILogger log, string nodeId)
    {
        public void Info(string message) => Write(LogLevel.Information, message);

        public void Warn(string message) => Write(LogLevel.Warning, message);

        private void Write(LogLevel level, string message)
        {
            if (log.IsEnabled(level))
            {
                log.Log(level, _workflowLogEvent, new LogState("AI.Agent: " + message, nodeId), null, static (s, _) => s.ToString());
            }
        }

        /// <summary>The same structured state as Core.Log: <c>Message</c> and <c>NodeId</c>.</summary>
        private sealed class LogState(string message, string node) : IReadOnlyList<KeyValuePair<string, object?>>
        {
            public int Count => 2;

            public KeyValuePair<string, object?> this[int index] => index == 0 ? new("Message", message) : new("NodeId", node);

            public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
            {
                yield return this[0];
                yield return this[1];
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

            public override string ToString() => message;
        }
    }
}
