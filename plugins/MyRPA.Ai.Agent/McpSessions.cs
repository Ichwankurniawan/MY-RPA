using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MyRPA.Ai.Agent;

/// <summary>
/// The MCP connections of one agent run (ADR-0048): opened on first use, only to configured servers, and closed when the
/// run's activity ends, so no session, process or authorization state is shared between runs. HTTP servers get no
/// redirects and only their configured URL; a token comes from the configured environment variable. Stdio servers run
/// exactly the configured command and arguments.
/// </summary>
internal sealed class McpSessions(AgentOptions options) : IAsyncDisposable
{
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.Ordinal);
    private readonly List<HttpClient> _http = [];

    /// <summary>The tools a server lists, as agent tools (each checks the server's configuration at every call).</summary>
    public async Task<IReadOnlyList<IAgentTool>> ToolsAsync(string server, CancellationToken cancellationToken)
    {
        var config = options.Servers[server];
        if (!config.Enabled)
        {
            throw new ToolFailureException($"the MCP server '{server}' is disabled in the configuration.");
        }

        var client = await ConnectAsync(config, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(config.ConnectTimeout);
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            return [.. tools.Select(t => (IAgentTool)new McpTool(this, config, t.ProtocolTool))];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ToolFailureException($"the MCP server '{server}' did not list its tools within {config.ConnectTimeout.TotalSeconds:0.#} s.");
        }
        catch (Exception ex) when (ex is McpException or IOException or HttpRequestException or InvalidOperationException)
        {
            await DropAsync(server).ConfigureAwait(false);
            throw new ToolFailureException($"the MCP server '{server}' could not list its tools: {ex.Message}", ex);
        }
    }

    /// <summary>Calls a tool; a broken connection is dropped (the next use reconnects) and reported as a tool failure.</summary>
    public async Task<CallToolResult> CallAsync(McpServerOptions config, string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        var client = await ConnectAsync(config, cancellationToken).ConfigureAwait(false);
        try
        {
            return await client.CallToolAsync(tool, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is McpException or IOException or HttpRequestException or InvalidOperationException)
        {
            await DropAsync(config.Name).ConfigureAwait(false);
            throw new ToolFailureException($"the MCP server '{config.Name}' failed: {ex.Message}", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
        }

        _clients.Clear();
        _http.ForEach(h => h.Dispose());
        _http.Clear();
    }

    private async Task<McpClient> ConnectAsync(McpServerOptions config, CancellationToken cancellationToken)
    {
        if (_clients.TryGetValue(config.Name, out var existing))
        {
            return existing;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(config.ConnectTimeout);
        try
        {
            var client = await McpClient.CreateAsync(Transport(config), new McpClientOptions { InitializationTimeout = config.ConnectTimeout }, cancellationToken: timeout.Token).ConfigureAwait(false);
            _clients[config.Name] = client;
            return client;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ToolFailureException($"the MCP server '{config.Name}' did not answer within {config.ConnectTimeout.TotalSeconds:0.#} s.");
        }
        catch (Exception ex) when (ex is McpException or IOException or HttpRequestException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new ToolFailureException($"the MCP server '{config.Name}' could not be reached: {ex.Message}", ex);
        }
    }

    private IClientTransport Transport(McpServerOptions config)
    {
        if (config.Transport == McpTransport.Stdio)
        {
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = config.Name,
                Command = config.Command!,
                Arguments = [.. config.Arguments],
                WorkingDirectory = config.WorkingDirectory,
                ShutdownTimeout = TimeSpan.FromSeconds(5),
            });
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (config.TokenVariable is { } variable)
        {
            var token = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(token))
            {
                throw new ToolFailureException($"the MCP server '{config.Name}' needs a token: set the environment variable {variable} for the server.");
            }

            headers["Authorization"] = "Bearer " + token;
        }

        // No redirects: the configured URL is the only place a request (and the token) goes.
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.Add(http);
        return new HttpClientTransport(
            new HttpClientTransportOptions { Name = config.Name, Endpoint = config.Url!, TransportMode = HttpTransportMode.StreamableHttp, AdditionalHeaders = headers, ConnectionTimeout = config.ConnectTimeout, MaxReconnectionAttempts = 0 },
            http,
            ownsHttpClient: false);
    }

    private async Task DropAsync(string server)
    {
        if (_clients.Remove(server, out var client))
        {
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
        }
    }

    private static async Task DisposeQuietlyAsync(McpClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is McpException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            // Closing a broken connection may fail; it is gone either way.
        }
    }

    /// <summary>An MCP server tool as an agent tool: <c>server.tool</c>, its description and input schema from the server (untrusted).</summary>
    private sealed class McpTool(McpSessions sessions, McpServerOptions config, Tool tool) : IAgentTool
    {
        public string Id { get; } = $"{config.Name}.{tool.Name}";

        public string Description { get; } = tool.Description ?? string.Empty;

        public JsonElement InputSchema { get; } = tool.InputSchema.ValueKind == JsonValueKind.Object ? tool.InputSchema.Clone() : JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

        public string Source => "mcp";

        public bool RequiresApproval => config.Approval.Contains(tool.Name);

        public string? Unavailable() =>
            !config.Enabled ? $"the MCP server '{config.Name}' is disabled; it was not run."
            : !config.Allows(tool.Name) ? $"the configuration does not allow the MCP tool '{tool.Name}'; it was not run."
            : null;

        public async Task<JsonNode?> InvokeAsync(JsonElement arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var values = arguments.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
            var result = await sessions.CallAsync(config, tool.Name, values, cancellationToken).ConfigureAwait(false);
            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
            if (result.IsError == true)
            {
                throw new ToolFailureException(text.Length > 0 ? text : $"the MCP tool '{tool.Name}' reported an error.");
            }

            return result.StructuredContent is { } structured
                ? JsonNode.Parse(JsonSerializer.Serialize(structured))
                : new JsonObject { ["text"] = text };
        }
    }
}
