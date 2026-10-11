namespace MyRPA.Ai.Contracts;

/// <summary>
/// An AI model behind a vendor-neutral boundary (ADR-0047). Provider plugins implement it; the host reads the registered
/// providers as a list. A provider only answers requests: it never runs anything and never sees the workflow engine.
/// </summary>
public interface IAiProvider
{
    /// <summary>Which provider and model answer, what they support, and whether they are configured.</summary>
    AiProviderInfo Info { get; }

    /// <summary>Sends one request and returns the model's answer.</summary>
    /// <param name="request">The instructions, the messages and the expected answer shape.</param>
    /// <param name="cancellationToken">Cancels the request; the provider then throws <see cref="OperationCanceledException"/>.</param>
    /// <returns>The answer text and its metadata.</returns>
    /// <exception cref="AiProviderException">The provider failed; <see cref="AiProviderException.Kind"/> says how.</exception>
    Task<AiCompletion> CompleteAsync(AiRequest request, CancellationToken cancellationToken);
}

/// <summary>A provider as the assistant and the Studio see it.</summary>
/// <param name="Id">Stable provider id, such as <c>openai-compatible</c>.</param>
/// <param name="Model">The model the provider uses.</param>
/// <param name="SupportsJsonSchema">Whether the provider can constrain an answer to a JSON schema.</param>
public sealed record AiProviderInfo(string Id, string Model, bool SupportsJsonSchema)
{
    /// <summary>Why the provider cannot answer now (for example, its key is not set); null when it can.</summary>
    public string? UnavailableReason { get; init; }
}

/// <summary>Who wrote a message.</summary>
public enum AiRole
{
    /// <summary>The person, or the assistant on the person's behalf (data such as the workflow).</summary>
    User = 0,

    /// <summary>A previous answer of the model.</summary>
    Assistant = 1,
}

/// <summary>One message of a request.</summary>
/// <param name="Role">Who wrote it.</param>
/// <param name="Text">Its text.</param>
public sealed record AiMessage(AiRole Role, string Text);

/// <summary>One request to a model.</summary>
/// <param name="Instructions">The system instructions (the rules the model follows).</param>
/// <param name="Messages">The conversation, oldest first.</param>
public sealed record AiRequest(string Instructions, IReadOnlyList<AiMessage> Messages)
{
    /// <summary>The JSON schema the answer must follow (JSON text); null for free text.</summary>
    public string? JsonSchema { get; init; }

    /// <summary>A short name for the schema (some providers require one).</summary>
    public string SchemaName { get; init; } = "answer";

    /// <summary>The longest answer to accept, in tokens.</summary>
    public int MaxOutputTokens { get; init; } = 4096;
}

/// <summary>Token counts, when the provider reports them.</summary>
/// <param name="InputTokens">Tokens read.</param>
/// <param name="OutputTokens">Tokens written.</param>
public sealed record AiUsage(int InputTokens, int OutputTokens);

/// <summary>A model's answer.</summary>
/// <param name="Text">The answer text (JSON when the request had a schema).</param>
/// <param name="Model">The model that answered.</param>
public sealed record AiCompletion(string Text, string Model)
{
    /// <summary>Token counts; null when the provider did not report them.</summary>
    public AiUsage? Usage { get; init; }
}

/// <summary>How a provider failed (ADR-0047). A failure is never a successful answer.</summary>
public enum AiFailureKind
{
    /// <summary>The provider is not configured (no endpoint, model or key).</summary>
    NotConfigured = 0,

    /// <summary>The provider refused the credentials.</summary>
    Authentication = 1,

    /// <summary>Too many requests, after the provider's bounded retries.</summary>
    RateLimited = 2,

    /// <summary>The provider could not be reached, or failed on its side, after bounded retries.</summary>
    Network = 3,

    /// <summary>The provider did not answer in time.</summary>
    Timeout = 4,

    /// <summary>The answer was missing, empty or not in the expected shape.</summary>
    InvalidResponse = 5,

    /// <summary>The provider or model cannot constrain answers to a schema.</summary>
    UnsupportedStructuredOutput = 6,

    /// <summary>The request or the answer was larger than allowed.</summary>
    TooLarge = 7,
}

/// <summary>A provider failure, with its kind. Messages never contain keys or request content.</summary>
public sealed class AiProviderException : Exception
{
    /// <summary>Creates an <see cref="AiFailureKind.InvalidResponse"/> failure.</summary>
    public AiProviderException()
        : this(AiFailureKind.InvalidResponse, "The AI provider failed.")
    {
    }

    /// <summary>Creates an <see cref="AiFailureKind.InvalidResponse"/> failure with a message.</summary>
    /// <param name="message">What happened.</param>
    public AiProviderException(string message)
        : this(AiFailureKind.InvalidResponse, message)
    {
    }

    /// <summary>Creates an <see cref="AiFailureKind.InvalidResponse"/> failure with a message and a cause.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="innerException">The cause.</param>
    public AiProviderException(string message, Exception innerException)
        : this(AiFailureKind.InvalidResponse, message, innerException)
    {
    }

    /// <summary>Creates a failure of a kind.</summary>
    /// <param name="kind">How the provider failed.</param>
    /// <param name="message">What happened, without keys or request content.</param>
    /// <param name="innerException">The cause, if any.</param>
    public AiProviderException(AiFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>How the provider failed.</summary>
    public AiFailureKind Kind { get; }
}
