using System.Globalization;
using MyRPA.Ai.Contracts;
using MyRPA.Sdk.Plugins;

namespace MyRPA.Ai.OpenAiCompatible;

/// <summary>The provider's settings (plugin configuration, ADR-0019). The key is never one of them.</summary>
/// <param name="Endpoint">The API base address, such as https://api.openai.com/v1 (requests go to its /chat/completions).</param>
/// <param name="Model">The model name.</param>
/// <param name="ApiKeyVariable">The environment variable that holds the key; null for an endpoint without a key.</param>
/// <param name="AuthHeader"><c>bearer</c> (Authorization: Bearer) or <c>api-key</c> (Azure OpenAI).</param>
/// <param name="ApiVersion">An <c>api-version</c> query value (Azure OpenAI); null for none.</param>
/// <param name="StructuredOutput"><c>json_schema</c>, <c>json_object</c> or <c>none</c>: how answers are constrained.</param>
/// <param name="MaxTokensParameter"><c>max_tokens</c> or <c>max_completion_tokens</c>.</param>
/// <param name="Timeout">The time one attempt may take.</param>
/// <param name="MaxRetries">Retries after a rate limit or a transient failure (never after a timeout or a refusal).</param>
/// <param name="RetryDelay">The wait before a retry when the endpoint does not say.</param>
/// <param name="MaxResponseBytes">The largest answer read.</param>
public sealed record OpenAiCompatibleOptions(
    Uri Endpoint,
    string Model,
    string? ApiKeyVariable,
    string AuthHeader,
    string? ApiVersion,
    string StructuredOutput,
    string MaxTokensParameter,
    TimeSpan Timeout,
    int MaxRetries,
    TimeSpan RetryDelay,
    long MaxResponseBytes);

/// <summary>Entry point of the OpenAI-compatible AI provider plugin (ADR-0047).</summary>
public sealed class OpenAiCompatiblePlugin : IPlugin
{
    private OpenAiCompatibleOptions? _options;

    /// <inheritdoc />
    public void Initialize(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings;
        string? Text(string name) => settings.TryGetValue(name, out var value) && value.Trim().Length > 0 ? value.Trim() : null;
        foreach (var name in new[] { "apiKey", "key", "token", "password", "secret" })
        {
            if (settings.ContainsKey(name))
            {
                throw new ArgumentException($"Setting '{name}' is not allowed: put the key in an environment variable and name it with 'apiKeyVariable', so it never sits in a configuration file.", nameof(context));
            }
        }

        var endpointText = Text("endpoint")
            ?? throw new ArgumentException("Setting 'endpoint' is required: the base address of an OpenAI-compatible API, such as https://api.openai.com/v1 or http://localhost:11434/v1.", nameof(context));
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)
            || !(endpoint.Scheme == Uri.UriSchemeHttps || (endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
            || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
        {
            throw new ArgumentException($"Setting 'endpoint' must be an https address, or an http address of this machine (localhost) for a local model, without credentials, query or fragment; '{endpointText}' is not.", nameof(context));
        }

        var model = Text("model") ?? throw new ArgumentException("Setting 'model' is required: the model name, such as gpt-4.1 or llama3.1.", nameof(context));
        var keyVariable = settings.TryGetValue("apiKeyVariable", out var variable) ? variable.Trim() : "OPENAI_API_KEY";
        if (keyVariable.Length > 0 && !keyVariable.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            throw new ArgumentException("Setting 'apiKeyVariable' must be the name of an environment variable (letters, digits and _), or empty for an endpoint without a key.", nameof(context));
        }

        _options = new OpenAiCompatibleOptions(
            endpoint,
            model,
            keyVariable.Length > 0 ? keyVariable : null,
            Choice(Text("authHeader"), "bearer", ["bearer", "api-key"], "authHeader"),
            Text("apiVersion"),
            Choice(Text("structuredOutput"), "json_schema", ["json_schema", "json_object", "none"], "structuredOutput"),
            Choice(Text("maxTokensParameter"), "max_tokens", ["max_tokens", "max_completion_tokens"], "maxTokensParameter"),
            TimeSpan.FromMilliseconds(Number(settings, "timeoutMs", 120_000, 1, 600_000)),
            (int)Number(settings, "maxRetries", 2, 0, 5),
            TimeSpan.FromMilliseconds(Number(settings, "retryDelayMs", 1_000, 0, 60_000)),
            Number(settings, "maxResponseBytes", 4L * 1024 * 1024, 1024, 64L * 1024 * 1024));
    }

    /// <inheritdoc />
    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        registrar
            .AddInstance(_options ?? throw new InvalidOperationException("Initialize must run before Register."))
            .AddService<IAiProvider, OpenAiCompatibleProvider>(PluginServiceLifetime.Plugin);
    }

    private static string Choice(string? value, string fallback, string[] allowed, string name) =>
        value is null ? fallback
        : allowed.Contains(value, StringComparer.OrdinalIgnoreCase) ? value.ToLowerInvariant()
        : throw new ArgumentException($"Setting '{name}' must be one of {string.Join(", ", allowed)}.", nameof(value));

    private static long Number(IReadOnlyDictionary<string, string> settings, string name, long fallback, long min, long max)
    {
        if (!settings.TryGetValue(name, out var text))
        {
            return fallback;
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : throw new ArgumentException($"Setting '{name}' must be a whole number from {min} to {max}.", nameof(settings));
    }
}
