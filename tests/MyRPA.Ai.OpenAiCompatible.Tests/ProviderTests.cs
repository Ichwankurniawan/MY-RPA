using System.Text.Json;
using MyRPA.Ai.Contracts;

namespace MyRPA.Ai.OpenAiCompatible.Tests;

/// <summary>The OpenAI-compatible provider against a local endpoint (ADR-0047, brief §5).</summary>
public sealed class ProviderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly AiRequest _request = new("You are a test.", [new AiMessage(AiRole.User, "hi"), new AiMessage(AiRole.Assistant, "earlier"), new AiMessage(AiRole.User, "again")])
    {
        JsonSchema = """{"type":"object","properties":{"a":{"type":"number"}}}""",
        SchemaName = "answer",
        MaxOutputTokens = 99,
    };

    private static Dictionary<string, string> Settings(FakeEndpoint endpoint, string keyVariable, params (string Name, string Value)[] more)
    {
        var settings = new Dictionary<string, string> { ["endpoint"] = endpoint.BaseUrl, ["model"] = "m1", ["apiKeyVariable"] = keyVariable, ["retryDelayMs"] = "0" };
        foreach (var (name, value) in more)
        {
            settings[name] = value;
        }

        return settings;
    }

    [Fact]
    public async Task Complete_SendsAChatCompletionWithTheSchema_AndReadsTheAnswer()
    {
        using var endpoint = new FakeEndpoint(new Reply(200, FakeEndpoint.Completion("""{"a":1}""")));
        await using var host = await ProviderHost.StartAsync(Settings(endpoint, ProviderHost.KeyVariable("test-key-123")));

        var completion = await host.Provider.CompleteAsync(_request, Token);

        Assert.Equal(("""{"a":1}""", "served-model"), (completion.Text, completion.Model));
        Assert.Equal(new AiUsage(12, 3), completion.Usage);
        var received = Assert.Single(endpoint.Received);
        Assert.Equal("/v1/chat/completions", received.PathAndQuery);
        Assert.Equal("Bearer test-key-123", received.Headers["authorization"]);
        var body = JsonDocument.Parse(received.Body).RootElement;
        Assert.Equal("m1", body.GetProperty("model").GetString());
        Assert.Equal(99, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal(["system:You are a test.", "user:hi", "assistant:earlier", "user:again"], body.GetProperty("messages").EnumerateArray().Select(m => $"{m.GetProperty("role").GetString()}:{m.GetProperty("content").GetString()}"));
        var format = body.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.Equal("object", format.GetProperty("json_schema").GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal(("openai-compatible", "m1", true, null), (host.Provider.Info.Id, host.Provider.Info.Model, host.Provider.Info.SupportsJsonSchema, host.Provider.Info.UnavailableReason));
    }

    [Fact]
    public async Task AMissingKey_MakesTheProviderUnavailable_AndNothingIsSent()
    {
        using var endpoint = new FakeEndpoint(new Reply(200, FakeEndpoint.Completion("{}")));
        var variable = ProviderHost.KeyVariable(null);
        await using var host = await ProviderHost.StartAsync(Settings(endpoint, variable));

        var error = await Assert.ThrowsAsync<AiProviderException>(() => host.Provider.CompleteAsync(_request, Token));

        Assert.Equal(AiFailureKind.NotConfigured, error.Kind);
        Assert.Contains(variable, host.Provider.Info.UnavailableReason, StringComparison.Ordinal);
        Assert.Empty(endpoint.Received);
    }

    [Fact]
    public async Task ALocalModelWithoutAKey_SendsNoCredentials()
    {
        using var endpoint = new FakeEndpoint(new Reply(200, FakeEndpoint.Completion("{}")));
        await using var host = await ProviderHost.StartAsync(Settings(endpoint, string.Empty));

        await host.Provider.CompleteAsync(_request, Token);

        Assert.False(Assert.Single(endpoint.Received).Headers.ContainsKey("authorization"));
        Assert.Null(host.Provider.Info.UnavailableReason);
    }

    [Fact]
    public async Task AzureStyle_UsesTheApiKeyHeaderAndTheApiVersion_AndTheNewerTokenParameter()
    {
        using var endpoint = new FakeEndpoint(new Reply(200, FakeEndpoint.Completion("{}")));
        await using var host = await ProviderHost.StartAsync(Settings(endpoint, ProviderHost.KeyVariable("azure-key"), ("authHeader", "api-key"), ("apiVersion", "2024-10-21"), ("maxTokensParameter", "max_completion_tokens")));

        await host.Provider.CompleteAsync(_request, Token);

        var received = Assert.Single(endpoint.Received);
        Assert.Equal("/v1/chat/completions?api-version=2024-10-21", received.PathAndQuery);
        Assert.Equal("azure-key", received.Headers["api-key"]);
        Assert.False(received.Headers.ContainsKey("authorization"));
        Assert.Equal(99, JsonDocument.Parse(received.Body).RootElement.GetProperty("max_completion_tokens").GetInt32());
    }

    [Theory]
    [InlineData("json_object", "json_object")]
    [InlineData("none", null)]
    public async Task WithoutSchemaSupport_TheSchemaGoesIntoTheInstructions(string mode, string? formatType)
    {
        using var endpoint = new FakeEndpoint(new Reply(200, FakeEndpoint.Completion("{}")));
        await using var host = await ProviderHost.StartAsync(Settings(endpoint, string.Empty, ("structuredOutput", mode)));

        await host.Provider.CompleteAsync(_request, Token);

        var body = JsonDocument.Parse(Assert.Single(endpoint.Received).Body).RootElement;
        Assert.Equal(formatType, body.TryGetProperty("response_format", out var format) ? format.GetProperty("type").GetString() : null);
        Assert.Contains("follows this JSON schema", body.GetProperty("messages")[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.False(host.Provider.Info.SupportsJsonSchema);
    }

    [Theory]
    [InlineData(401, AiFailureKind.Authentication)]
    [InlineData(403, AiFailureKind.Authentication)]
    [InlineData(404, AiFailureKind.NotConfigured)]
    [InlineData(413, AiFailureKind.TooLarge)]
    [InlineData(418, AiFailureKind.InvalidResponse)]
    [InlineData(302, AiFailureKind.Network)]
    public async Task Refusals_MapToTheirKind_AreNotRetried_AndNeverShowTheKey(int status, AiFailureKind kind)
    {
        using var endpoint = new FakeEndpoint(new Reply(status, """{"error":{"message":"nope"}}""", Location: "https://elsewhere.example/v1/chat/completions"));
        await using var host = await ProviderHost.StartAsync(Settings(endpoint, ProviderHost.KeyVariable("very-secret-key")));

        var error = await Assert.ThrowsAsync<AiProviderException>(() => host.Provider.CompleteAsync(_request, Token));

        Assert.Equal(kind, error.Kind);
        Assert.Single(endpoint.Received);
        Assert.DoesNotContain("very-secret-key", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEndpointWithoutSchemaSupport_SaysSo()
    {
        using var endpoint = new FakeEndpoint(new Reply(400, """{"error":{"message":"Invalid parameter: response_format of type json_schema is not supported"}}"""));
        await using var host = await ProviderHost.StartAsync(Settings(endpoint, string.Empty));

        var error = await Assert.ThrowsAsync<AiProviderException>(() => host.Provider.CompleteAsync(_request, Token));

        Assert.Equal(AiFailureKind.UnsupportedStructuredOutput, error.Kind);
        Assert.Contains("structuredOutput", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RateLimits_AreRetried_WithinTheLimit()
    {
        using var recovering = new FakeEndpoint(new Reply(429, "{}", RetryAfter: "0"), new Reply(429, "{}", RetryAfter: "0"), new Reply(200, FakeEndpoint.Completion("{}")));
        await using var host = await ProviderHost.StartAsync(Settings(recovering, string.Empty, ("maxRetries", "2")));
        using var limited = new FakeEndpoint(new Reply(429, "{}", RetryAfter: "0"));
        await using var limitedHost = await ProviderHost.StartAsync(Settings(limited, string.Empty, ("maxRetries", "2")));

        await host.Provider.CompleteAsync(_request, Token);
        var error = await Assert.ThrowsAsync<AiProviderException>(() => limitedHost.Provider.CompleteAsync(_request, Token));

        Assert.Equal(3, recovering.Received.Count);
        Assert.Equal(AiFailureKind.RateLimited, error.Kind);
        Assert.Equal(3, limited.Received.Count);
    }

    [Fact]
    public async Task ServerFailures_AreRetried_ThenReportedAsNetwork()
    {
        using var endpoint = new FakeEndpoint(new Reply(503, "{}"));
        await using var host = await ProviderHost.StartAsync(Settings(endpoint, string.Empty, ("maxRetries", "1")));

        var error = await Assert.ThrowsAsync<AiProviderException>(() => host.Provider.CompleteAsync(_request, Token));

        Assert.Equal(AiFailureKind.Network, error.Kind);
        Assert.Equal(2, endpoint.Received.Count);
    }

    [Fact]
    public async Task AnUnreachableEndpoint_IsANetworkFailure()
    {
        var settings = new Dictionary<string, string> { ["endpoint"] = $"http://localhost:{FakeEndpoint.FreePort()}/v1", ["model"] = "m", ["apiKeyVariable"] = string.Empty, ["maxRetries"] = "1", ["retryDelayMs"] = "0" };
        await using var host = await ProviderHost.StartAsync(settings);

        var error = await Assert.ThrowsAsync<AiProviderException>(() => host.Provider.CompleteAsync(_request, Token));

        Assert.Equal(AiFailureKind.Network, error.Kind);
    }

    [Fact]
    public async Task ASlowEndpoint_TimesOut_WithoutARetry()
    {
        using var endpoint = new FakeEndpoint(new Reply(200, FakeEndpoint.Completion("{}"), DelayMs: 10_000));
        await using var host = await ProviderHost.StartAsync(Settings(endpoint, string.Empty, ("timeoutMs", "1000"), ("maxRetries", "2")));

        var error = await Assert.ThrowsAsync<AiProviderException>(() => host.Provider.CompleteAsync(_request, Token));

        // Under load the attempt may time out before the request even arrives; either way there is no retry.
        Assert.Equal(AiFailureKind.Timeout, error.Kind);
        Assert.True(endpoint.Received.Count <= 1, $"{endpoint.Received.Count} requests");
    }

    [Fact]
    public async Task Cancellation_StopsTheRequest()
    {
        using var endpoint = new FakeEndpoint(new Reply(200, FakeEndpoint.Completion("{}"), DelayMs: 5_000));
        await using var host = await ProviderHost.StartAsync(Settings(endpoint, string.Empty));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancel.CancelAfter(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Provider.CompleteAsync(_request, cancel.Token));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{"message":{"content":""}}]}""")]
    [InlineData("""{"choices":[{"message":{"content":"{\"a\":"},"finish_reason":"length"}]}""")]
    [InlineData("""{"choices":[{"message":{"content":null,"refusal":"I cannot help with that."}}]}""")]
    public async Task UnusableAnswers_AreInvalidResponses(string body)
    {
        using var endpoint = new FakeEndpoint(new Reply(200, body));
        await using var host = await ProviderHost.StartAsync(Settings(endpoint, string.Empty));

        var error = await Assert.ThrowsAsync<AiProviderException>(() => host.Provider.CompleteAsync(_request, Token));

        Assert.Equal(AiFailureKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public async Task AnOversizedAnswer_IsRefused()
    {
        using var endpoint = new FakeEndpoint(new Reply(200, FakeEndpoint.Completion(new string('x', 5_000))));
        await using var host = await ProviderHost.StartAsync(Settings(endpoint, string.Empty, ("maxResponseBytes", "1024")));

        var error = await Assert.ThrowsAsync<AiProviderException>(() => host.Provider.CompleteAsync(_request, Token));

        Assert.Equal(AiFailureKind.TooLarge, error.Kind);
    }

    [Theory]
    [InlineData("endpoint", "http://example.com/v1", "https address")]
    [InlineData("endpoint", "https://user:pass@api.example.com/v1", "https address")]
    [InlineData("apiKey", "sk-123", "environment variable")]
    [InlineData("model", "", "'model' is required")]
    [InlineData("structuredOutput", "xml", "structuredOutput")]
    [InlineData("apiKeyVariable", "MY-KEY", "apiKeyVariable")]
    public async Task UnsafeOrIncompleteSettings_AreRefused(string name, string value, string reason)
    {
        var settings = new Dictionary<string, string> { ["endpoint"] = "https://api.example.com/v1", ["model"] = "m", [name] = value };
        await using var host = await ProviderHost.StartAsync(settings, expectFailure: true);

        Assert.True(host.Plugins.HasRequiredFailures);
        Assert.Contains(host.Plugins.Diagnostics, d => d.Message.Contains(reason, StringComparison.Ordinal));
    }
}
