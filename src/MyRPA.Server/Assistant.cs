using System.Text.Json;
using MyRPA.Ai.Assistant;
using MyRPA.Ai.Contracts;
using MyRPA.Core.Activities;
using MyRPA.Execution.Hosting;

namespace MyRPA.Server;

/// <summary>
/// The AI Workflow Assistant of this server (ADR-0047): the first AI provider a plugin registered, over the host's
/// activity catalog. Without a provider the assistant is unavailable and says why.
/// </summary>
internal sealed class AssistantHost
{
    public AssistantHost(IEnumerable<IAiProvider> providers, IActivityCatalog catalog, ILoggerFactory loggers, TimeProvider time)
    {
        Provider = providers.FirstOrDefault();
        Assistant = Provider is null ? null : new WorkflowAssistant(Provider, catalog, loggers.CreateLogger<WorkflowAssistant>(), time);
    }

    public IAiProvider? Provider { get; }

    public WorkflowAssistant? Assistant { get; }

    /// <summary>Why the assistant cannot answer now; null when it can.</summary>
    public string? Unavailable => Provider is null
        ? "No AI provider is configured: add an AI provider plugin (such as MyRPA.Ai.OpenAiCompatible) to the server's plugin configuration."
        : Provider.Info.UnavailableReason;
}

/// <summary>
/// <c>/api/assistant</c> (ADR-0047): availability, what would be sent, and the assistant's operations. Every answer is
/// advice or a proposal for review; nothing here saves, applies or runs anything.
/// </summary>
internal static class AssistantEndpoints
{
    private const int MaxConversationTurns = 20;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/assistant", (AssistantHost host) => Results.Json(new
        {
            available = host.Unavailable is null,
            provider = host.Provider?.Info.Id,
            model = host.Provider?.Info.Model,
            reason = host.Unavailable,
        }));

        api.MapPost("/assistant/preview", (AssistantRequest request, AssistantHost host, ExecutionHost runs, HttpContext context) =>
            Answer(host, requireAvailable: false, assistant => Task.FromResult<object>(RequestJson((request.Operation ?? "generate") switch
            {
                "generate" => assistant.PrepareGeneration(Generation(request, runs, context)),
                "explain" => assistant.PrepareExplain(new ExplainRequest(Document(request))),
                "debug" => assistant.PrepareDebug(new DebugRequest(Document(request), Failure(request.RunId, runs))),
                var other => throw new RequestException(StatusCodes.Status400BadRequest, $"'operation' must be generate, explain or debug, not '{other}'."),
            }))));

        api.MapPost("/assistant/generate", (AssistantRequest request, AssistantHost host, ExecutionHost runs, HttpContext context) =>
            Answer(host, requireAvailable: true, async assistant => GenerationJson(await assistant.GenerateAsync(Generation(request, runs, context), context.RequestAborted).ConfigureAwait(false))));

        api.MapPost("/assistant/explain", (AssistantRequest request, AssistantHost host, HttpContext context) =>
            Answer(host, requireAvailable: true, async assistant =>
            {
                var result = await assistant.ExplainAsync(new ExplainRequest(Document(request)) { CorrelationId = context.TraceIdentifier }, context.RequestAborted).ConfigureAwait(false);
                return new { summary = result.Summary, steps = result.Steps.Select(s => new { nodeId = s.NodeId, text = s.Text }), usage = UsageJson(result.Usage) };
            }));

        api.MapPost("/assistant/debug", (AssistantRequest request, AssistantHost host, ExecutionHost runs, HttpContext context) =>
            Answer(host, requireAvailable: true, async assistant =>
            {
                var debug = new DebugRequest(Document(request), Failure(request.RunId, runs)) { CorrelationId = context.TraceIdentifier };
                var result = await assistant.DebugAsync(debug, context.RequestAborted).ConfigureAwait(false);
                return new
                {
                    explanation = result.Explanation,
                    certainty = result.Certainty.ToString().ToLowerInvariant(),
                    nodeId = result.NodeId,
                    suggestions = result.Suggestions,
                    failure = new { status = debug.Failure.Status, code = debug.Failure.Code, nodeId = debug.Failure.NodeId, activityType = debug.Failure.ActivityType, errorType = debug.Failure.ErrorType },
                    usage = UsageJson(result.Usage),
                };
            }));
    }

    private static async Task<IResult> Answer(AssistantHost host, bool requireAvailable, Func<WorkflowAssistant, Task<object>> action)
    {
        if (host.Assistant is null || (requireAvailable && host.Unavailable is not null))
        {
            return Results.Json(new { error = host.Unavailable, kind = nameof(AiFailureKind.NotConfigured) }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            return Results.Json(await action(host.Assistant).ConfigureAwait(false));
        }
        catch (RequestException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
        }
        catch (ArgumentException ex)
        {
            return Results.Json(new { error = ex.ParamName is null ? ex.Message : ex.Message.Replace($" (Parameter '{ex.ParamName}')", string.Empty, StringComparison.Ordinal) }, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (AiProviderException ex)
        {
            // A provider failure is never a successful answer (brief §5); the kind tells the Studio what to say.
            var status = ex.Kind switch
            {
                AiFailureKind.NotConfigured => StatusCodes.Status503ServiceUnavailable,
                AiFailureKind.RateLimited => StatusCodes.Status429TooManyRequests,
                AiFailureKind.Timeout => StatusCodes.Status504GatewayTimeout,
                AiFailureKind.TooLarge => StatusCodes.Status413PayloadTooLarge,
                _ => StatusCodes.Status502BadGateway,
            };
            return Results.Json(new { error = ex.Message, kind = ex.Kind.ToString() }, statusCode: status);
        }
    }

    private static GenerationRequest Generation(AssistantRequest request, ExecutionHost runs, HttpContext context)
    {
        var purpose = (request.Purpose ?? "create").ToUpperInvariant() switch
        {
            "CREATE" => AssistantPurpose.Create,
            "EDIT" => AssistantPurpose.Edit,
            "FIXVALIDATION" => AssistantPurpose.FixValidation,
            "FIXFAILURE" => AssistantPurpose.FixFailure,
            _ => throw new RequestException(StatusCodes.Status400BadRequest, "'purpose' must be create, edit, fixValidation or fixFailure."),
        };
        var turns = request.Conversation ?? [];
        if (turns.Count > MaxConversationTurns)
        {
            throw new RequestException(StatusCodes.Status400BadRequest, $"The conversation may have at most {MaxConversationTurns} turns; start a new request.");
        }

        return new GenerationRequest(request.Request ?? string.Empty, purpose)
        {
            Document = purpose == AssistantPurpose.Create ? null : Document(request),
            Conversation = [.. turns.Select(t => new AssistantTurn(
                string.Equals(t?.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? AiRole.Assistant : AiRole.User,
                t?.Text is { Length: > 0 } text ? text : throw new RequestException(StatusCodes.Status400BadRequest, "Every conversation turn needs a 'text'.")))],
            Failure = purpose == AssistantPurpose.FixFailure ? Failure(request.RunId, runs) : null,
            CorrelationId = context.TraceIdentifier,
        };
    }

    private static string Document(AssistantRequest request) =>
        request.Document is { ValueKind: JsonValueKind.Object } document
            ? document.GetRawText()
            : throw new RequestException(StatusCodes.Status400BadRequest, "'document' must be a workflow JSON object.");

    /// <summary>The safe failure details of a finished run of this server (never its values).</summary>
    private static RunFailure Failure(string? runId, ExecutionHost runs)
    {
        if (runId is null || !runs.TryGet(runId, out var run))
        {
            throw new RequestException(StatusCodes.Status404NotFound, $"Unknown run '{runId}'; it may have expired. Run the workflow again.");
        }

        if (!run.Completion.IsCompletedSuccessfully)
        {
            throw new RequestException(StatusCodes.Status409Conflict, "The run has not finished.");
        }

        var result = run.Completion.Result;
        return result.Error is { } error
            ? new RunFailure(result.Status.ToString()) { Code = error.Code, Message = error.Message, NodeId = error.NodeId, ActivityType = error.ActivityType, ErrorType = error.ErrorType }
            : throw new RequestException(StatusCodes.Status409Conflict, $"The run did not fail ({result.Status}).");
    }

    private static object GenerationJson(GenerationResult result) => new
    {
        outcome = result.Outcome.ToString().ToLowerInvariant(),
        known = result.Known,
        assumptions = result.Assumptions,
        questions = result.Questions.Select(q => new { question = q.Question, options = q.Options }),
        missing = result.Missing,
        plan = result.Plan,
        summary = result.Summary,
        document = result.Document,
        diagnostics = result.Diagnostics.Select(d => new { code = d.Code, severity = d.Severity, message = d.Message, path = d.Path, nodeId = d.NodeId }),
        repairAttempts = result.RepairAttempts,
        activityTypes = result.ActivityTypes,
        sideEffects = result.SideEffects,
        usage = UsageJson(result.Usage),
    };

    private static object RequestJson(AiRequest request) => new
    {
        instructions = request.Instructions,
        messages = request.Messages.Select(m => new { role = m.Role.ToString().ToLowerInvariant(), text = m.Text }),
        schema = request.JsonSchema,
    };

    private static object? UsageJson(AssistantUsage? usage) => usage is null ? null : new
    {
        provider = usage.Provider,
        model = usage.Model,
        requests = usage.Requests,
        inputTokens = usage.InputTokens,
        outputTokens = usage.OutputTokens,
        durationMs = usage.DurationMs,
    };

    /// <summary>A refused request, with its HTTP status.</summary>
    private sealed class RequestException(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }
}

/// <summary>A conversation turn of an assistant request.</summary>
internal sealed record AssistantTurnRequest(string? Role, string? Text);

/// <summary>Body of the <c>/api/assistant/*</c> requests (ADR-0047).</summary>
internal sealed record AssistantRequest(
    string? Operation,
    string? Request,
    string? Purpose,
    JsonElement? Document,
    List<AssistantTurnRequest?>? Conversation,
    string? RunId);
