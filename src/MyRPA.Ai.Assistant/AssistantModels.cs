using MyRPA.Ai.Contracts;

namespace MyRPA.Ai.Assistant;

/// <summary>Limits of the assistant (ADR-0047): every loop, request and answer is bounded.</summary>
public sealed record AssistantOptions
{
    /// <summary>How many times an invalid proposal is sent back for correction (0 = never).</summary>
    public int MaxRepairAttempts { get; init; } = 2;

    /// <summary>The longest answer accepted from the model, in characters.</summary>
    public int MaxAnswerChars { get; init; } = 200_000;

    /// <summary>The largest request sent to the model, in characters (instructions and messages).</summary>
    public int MaxRequestChars { get; init; } = 600_000;

    /// <summary>How much of a run's error message is sent (after redaction).</summary>
    public int MaxErrorMessageChars { get; init; } = 2_000;

    /// <summary>The longest answer the model may write, in tokens.</summary>
    public int MaxOutputTokens { get; init; } = 8_192;
}

/// <summary>What a generation request asks for.</summary>
public enum AssistantPurpose
{
    /// <summary>A new workflow from a description.</summary>
    Create = 0,

    /// <summary>A change to the given workflow.</summary>
    Edit = 1,

    /// <summary>A correction of the given workflow's validation problems (computed by the validator, not sent by the caller).</summary>
    FixValidation = 2,

    /// <summary>A correction of the given workflow after a failed run.</summary>
    FixFailure = 3,
}

/// <summary>One earlier turn of the conversation (an answer of the assistant, or the person's reply).</summary>
/// <param name="Role">Who wrote it.</param>
/// <param name="Text">Its text.</param>
public sealed record AssistantTurn(AiRole Role, string Text);

/// <summary>
/// The safe part of a failed run (brief §10): the status and the error's code, message (redacted and shortened), node,
/// activity type and error type. Never run values: run events carry none (ADR-0023).
/// </summary>
/// <param name="Status">The run's final status (Failed, TimedOut, Cancelled).</param>
public sealed record RunFailure(string Status)
{
    /// <summary>The error code, such as MYRPA2002.</summary>
    public string? Code { get; init; }

    /// <summary>The error message (redacted before it is sent).</summary>
    public string? Message { get; init; }

    /// <summary>The node that failed.</summary>
    public string? NodeId { get; init; }

    /// <summary>The failed node's activity type.</summary>
    public string? ActivityType { get; init; }

    /// <summary>The classified error type, such as Timeout or HttpStatus.</summary>
    public string? ErrorType { get; init; }
}

/// <summary>A request to propose a new workflow or a change.</summary>
/// <param name="Request">The person's words (may be empty for a validation fix).</param>
/// <param name="Purpose">What is asked.</param>
public sealed record GenerationRequest(string Request, AssistantPurpose Purpose)
{
    /// <summary>Earlier turns: the assistant's questions and the person's answers, oldest first.</summary>
    public IReadOnlyList<AssistantTurn> Conversation { get; init; } = [];

    /// <summary>The current workflow (JSON object text); required unless <see cref="Purpose"/> is Create.</summary>
    public string? Document { get; init; }

    /// <summary>The failed run, for <see cref="AssistantPurpose.FixFailure"/>.</summary>
    public RunFailure? Failure { get; init; }

    /// <summary>Correlates the request in logs (never logged with content).</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>What the assistant answered to a generation request.</summary>
public enum GenerationOutcome
{
    /// <summary>It needs answers first (<see cref="GenerationResult.Questions"/>).</summary>
    Clarify = 0,

    /// <summary>The listed activities cannot do it (<see cref="GenerationResult.Missing"/>).</summary>
    Unsupported = 1,

    /// <summary>A workflow that passed every deterministic check.</summary>
    Proposal = 2,

    /// <summary>A workflow that still failed the checks after the bounded repairs; never applied.</summary>
    Invalid = 3,
}

/// <summary>A focused question, with short options when there are obvious ones.</summary>
/// <param name="Question">The question.</param>
/// <param name="Options">Suggested answers (may be empty).</param>
public sealed record ClarifyingQuestion(string Question, IReadOnlyList<string> Options);

/// <summary>A problem found by the validator or by the assistant's own checks (codes AI0001–AI0006).</summary>
/// <param name="Code">The code, such as MYRPA1033 or AI0004.</param>
/// <param name="Severity">Error or Warning.</param>
/// <param name="Message">What is wrong.</param>
public sealed record AssistantDiagnostic(string Code, string Severity, string Message)
{
    /// <summary>Where in the workflow (a JSON path), when known.</summary>
    public string? Path { get; init; }

    /// <summary>The node concerned, when known.</summary>
    public string? NodeId { get; init; }
}

/// <summary>How much an operation used: requests, tokens and time.</summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Model">The model.</param>
/// <param name="Requests">How many requests were sent (1 plus repairs).</param>
/// <param name="InputTokens">Tokens read, when reported.</param>
/// <param name="OutputTokens">Tokens written, when reported.</param>
/// <param name="DurationMs">Wall time.</param>
public sealed record AssistantUsage(string Provider, string Model, int Requests, int InputTokens, int OutputTokens, long DurationMs);

/// <summary>The answer to a generation request.</summary>
/// <param name="Outcome">What kind of answer it is.</param>
public sealed record GenerationResult(GenerationOutcome Outcome)
{
    /// <summary>Requirements the assistant took as known.</summary>
    public IReadOnlyList<string> Known { get; init; } = [];

    /// <summary>Assumptions it made (shown for review, never silently).</summary>
    public IReadOnlyList<string> Assumptions { get; init; } = [];

    /// <summary>Questions it needs answered (Clarify).</summary>
    public IReadOnlyList<ClarifyingQuestion> Questions { get; init; } = [];

    /// <summary>Capabilities the catalog lacks (Unsupported).</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>The plan in plain words.</summary>
    public IReadOnlyList<string> Plan { get; init; } = [];

    /// <summary>The model's description of its answer (labelled as such in the Studio).</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>The proposed workflow (indented JSON), or the last invalid one; null for Clarify and Unsupported.</summary>
    public string? Document { get; init; }

    /// <summary>Why the proposal is invalid (Invalid), or the validator's warnings (Proposal).</summary>
    public IReadOnlyList<AssistantDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>How many corrections were requested.</summary>
    public int RepairAttempts { get; init; }

    /// <summary>The activity types the workflow uses, read from the workflow itself.</summary>
    public IReadOnlyList<string> ActivityTypes { get; init; } = [];

    /// <summary>The side effects of those activities, from the catalog (FileSystem, Network, Browser).</summary>
    public IReadOnlyList<string> SideEffects { get; init; } = [];

    /// <summary>What the operation used.</summary>
    public AssistantUsage? Usage { get; init; }
}

/// <summary>A request to explain a workflow.</summary>
/// <param name="Document">The workflow (JSON object text).</param>
public sealed record ExplainRequest(string Document)
{
    /// <summary>Correlates the request in logs.</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>An explanation of one node.</summary>
/// <param name="NodeId">A node of the workflow (ids that are not in it are dropped).</param>
/// <param name="Text">What the node does.</param>
public sealed record NodeNote(string NodeId, string Text);

/// <summary>An explanation of a workflow.</summary>
/// <param name="Summary">What the workflow does.</param>
/// <param name="Steps">Notes on its important nodes, in order.</param>
public sealed record ExplainResult(string Summary, IReadOnlyList<NodeNote> Steps)
{
    /// <summary>What the operation used.</summary>
    public AssistantUsage? Usage { get; init; }
}

/// <summary>A request to explain a failed run.</summary>
/// <param name="Document">The workflow that ran (JSON object text).</param>
/// <param name="Failure">The run's safe failure details.</param>
public sealed record DebugRequest(string Document, RunFailure Failure)
{
    /// <summary>Correlates the request in logs.</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>How sure an explanation of a failure is (brief §10: hypotheses are labelled as such).</summary>
public enum DebugCertainty
{
    /// <summary>The failure details show the cause directly.</summary>
    Likely = 0,

    /// <summary>A plausible cause the details do not prove.</summary>
    Possible = 1,

    /// <summary>The details do not point to a cause.</summary>
    Unknown = 2,
}

/// <summary>An explanation of a failed run.</summary>
/// <param name="Explanation">The likely or possible cause.</param>
/// <param name="Certainty">How sure it is.</param>
public sealed record DebugResult(string Explanation, DebugCertainty Certainty)
{
    /// <summary>The node it points to (only a node of the workflow).</summary>
    public string? NodeId { get; init; }

    /// <summary>Changes the person could make (advice; a fix is a separate, validated proposal).</summary>
    public IReadOnlyList<string> Suggestions { get; init; } = [];

    /// <summary>What the operation used.</summary>
    public AssistantUsage? Usage { get; init; }
}
