using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyRPA.Ai.Contracts;

namespace MyRPA.Ai.OpenAiCompatible;

/// <summary>
/// An <see cref="IAiProvider"/> for OpenAI-compatible chat completions (ADR-0047). One endpoint only: HTTPS (plain HTTP
/// only to this machine), no redirects, TLS validation as the platform does it (never turned off). The key is read from
/// the configured environment variable for each request, sent only to that endpoint, and never written to a message.
/// Bounded retries only for rate limits and transient failures; a timeout per attempt.
/// </summary>
public sealed class OpenAiCompatibleProvider : IAiProvider, IDisposable
{
    /// <summary>The provider id.</summary>
    public const string ProviderId = "openai-compatible";

    private static readonly TimeSpan _maxRetryAfter = TimeSpan.FromSeconds(60);
    private readonly OpenAiCompatibleOptions _options;
    private readonly HttpClient _client;

    /// <summary>Creates the provider (the plugin host creates it once per plugin).</summary>
    /// <param name="options">The plugin's settings.</param>
    public OpenAiCompatibleProvider(OpenAiCompatibleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <inheritdoc />
    public AiProviderInfo Info => new(ProviderId, _options.Model, _options.StructuredOutput == "json_schema") { UnavailableReason = KeyMissing() };

    /// <inheritdoc />
    public async Task<AiCompletion> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (KeyMissing() is { } reason)
        {
            throw new AiProviderException(AiFailureKind.NotConfigured, reason);
        }

        var body = Body(request);
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (completion, retryAfter, failure) = await AttemptAsync(body, cancellationToken).ConfigureAwait(false);
            if (completion is not null)
            {
                return completion;
            }

            if (retryAfter is not { } delay || attempt >= _options.MaxRetries)
            {
                throw failure!;
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    private string? Key => _options.ApiKeyVariable is { } name ? Environment.GetEnvironmentVariable(name) : null;

    private string? KeyMissing() =>
        _options.ApiKeyVariable is { } name && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))
            ? $"The AI provider's key is not set: set the environment variable {name} for the server and restart it (or set apiKeyVariable to empty for an endpoint without a key)."
            : null;

    private Uri Url() => new(_options.Endpoint.AbsoluteUri.TrimEnd('/') + "/chat/completions"
        + (_options.ApiVersion is { } version ? "?api-version=" + Uri.EscapeDataString(version) : string.Empty));

    /// <summary>One attempt: the answer, or the failure and (for a retryable one) when to try again.</summary>
    private async Task<(AiCompletion? Completion, TimeSpan? RetryAfter, AiProviderException? Failure)> AttemptAsync(string body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        using var message = new HttpRequestMessage(HttpMethod.Post, Url()) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (Key is { Length: > 0 } key)
        {
            if (_options.AuthHeader == "api-key")
            {
                message.Headers.Add("api-key", key);
            }
            else
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }
        }

        try
        {
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var text = await ReadAsync(response, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
            {
                return (Parse(text), null, null);
            }

            var detail = ErrorDetail(text);
            return status switch
            {
                401 or 403 => (null, null, new AiProviderException(AiFailureKind.Authentication, $"The AI endpoint refused the credentials ({status}){detail}.")),
                429 => (null, RetryAfter(response), new AiProviderException(AiFailureKind.RateLimited, $"The AI endpoint is limiting requests (429){detail}.")),
                404 => (null, null, new AiProviderException(AiFailureKind.NotConfigured, $"The AI endpoint or model was not found (404){detail}.")),
                413 => (null, null, new AiProviderException(AiFailureKind.TooLarge, $"The AI endpoint refused the request as too large (413){detail}.")),
                400 or 422 when MentionsStructuredOutput(text) => (null, null, new AiProviderException(AiFailureKind.UnsupportedStructuredOutput, $"The model or endpoint does not support JSON-schema answers{detail}; set the provider's 'structuredOutput' setting to json_object or none.")),
                >= 300 and < 400 => (null, null, new AiProviderException(AiFailureKind.Network, $"The AI endpoint redirected ({status}); redirects are not followed, so configure the final address as the endpoint.")),
                >= 500 => (null, _options.RetryDelay, new AiProviderException(AiFailureKind.Network, $"The AI endpoint failed ({status}){detail}.")),
                _ => (null, null, new AiProviderException(AiFailureKind.InvalidResponse, $"The AI endpoint answered {status}{detail}.")),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, null, new AiProviderException(AiFailureKind.Timeout, string.Create(CultureInfo.InvariantCulture, $"The AI endpoint did not answer within {_options.Timeout.TotalSeconds:0.#} seconds.")));
        }
        catch (HttpRequestException ex)
        {
            return (null, _options.RetryDelay, new AiProviderException(AiFailureKind.Network, $"The AI endpoint could not be reached: {ex.Message}", ex));
        }
    }

    private async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > _options.MaxResponseBytes)
        {
            throw new AiProviderException(AiFailureKind.TooLarge, $"The AI endpoint's answer is larger than {_options.MaxResponseBytes} bytes.");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > _options.MaxResponseBytes)
                {
                    throw new AiProviderException(AiFailureKind.TooLarge, $"The AI endpoint's answer is larger than {_options.MaxResponseBytes} bytes.");
                }

                buffer.Write(chunk, 0, read);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
    }

    private string Body(AiRequest request)
    {
        var instructions = request.Instructions;
        if (request.JsonSchema is { } schema && _options.StructuredOutput != "json_schema")
        {
            // Without JSON-schema support the schema goes into the instructions; the assistant validates the answer anyway.
            instructions += "\n\nAnswer with only a JSON object that follows this JSON schema:\n" + schema;
        }

        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = instructions } };
        foreach (var message in request.Messages)
        {
            messages.Add(new JsonObject { ["role"] = message.Role == AiRole.Assistant ? "assistant" : "user", ["content"] = message.Text });
        }

        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["messages"] = messages,
            [_options.MaxTokensParameter] = request.MaxOutputTokens,
        };
        if (request.JsonSchema is { } jsonSchema && _options.StructuredOutput != "none")
        {
            body["response_format"] = _options.StructuredOutput == "json_schema"
                ? new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject { ["name"] = request.SchemaName, ["schema"] = JsonNode.Parse(jsonSchema), ["strict"] = true },
                }
                : new JsonObject { ["type"] = "json_object" };
        }

        return body.ToJsonString();
    }

    private AiCompletion Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                throw new AiProviderException(AiFailureKind.InvalidResponse, "The AI endpoint's answer has no choices.");
            }

            var choice = choices[0];
            var message = choice.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
            if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String && refusal.GetString() is { Length: > 0 } reason)
            {
                throw new AiProviderException(AiFailureKind.InvalidResponse, $"The model declined to answer: {Shorten(reason)}");
            }

            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String && finish.GetString() == "length")
            {
                throw new AiProviderException(AiFailureKind.InvalidResponse, "The model's answer was cut off at its output limit.");
            }

            var content = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new AiProviderException(AiFailureKind.InvalidResponse, "The AI endpoint's answer is empty.");
            }

            var model = root.TryGetProperty("model", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : _options.Model;
            AiUsage? usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object
                ? new AiUsage(Int(u, "prompt_tokens"), Int(u, "completion_tokens"))
                : null;
            return new AiCompletion(content, model) { Usage = usage };
        }
        catch (JsonException ex)
        {
            throw new AiProviderException(AiFailureKind.InvalidResponse, "The AI endpoint's answer is not JSON.", ex);
        }
    }

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : _options.RetryDelay);
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait > _maxRetryAfter ? _maxRetryAfter : wait;
    }

    private static bool MentionsStructuredOutput(string text) =>
        text.Contains("response_format", StringComparison.OrdinalIgnoreCase) || text.Contains("json_schema", StringComparison.OrdinalIgnoreCase);

    /// <summary>The endpoint's own error message (short), never the request.</summary>
    private static string ErrorDetail(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("error", out var error))
            {
                return string.Empty;
            }

            var message = error.ValueKind == JsonValueKind.String ? error : error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) ? m : default;
            return message.ValueKind == JsonValueKind.String ? ": " + Shorten(message.GetString()!) : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string Shorten(string text) => text.Length <= 300 ? text : string.Concat(text.AsSpan(0, 300), "…");
}
