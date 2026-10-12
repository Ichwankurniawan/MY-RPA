using System.Diagnostics;
using System.Text.Json;
using MyRPA.Ai.Contracts;

namespace MyRPA.Ai.Agent;

/// <summary>
/// The agent plugin's tools as the Studio shows them (ADR-0048 amendment): the built-ins and the configured MCP servers.
/// Discovery opens its own short-lived session (closed before returning, so a stdio server's process ends) and runs one
/// at a time. Tokens are never exposed: only whether their environment variable is set.
/// </summary>
public sealed class AgentToolCatalog : IAgentToolCatalog, IDisposable
{
    private readonly AgentOptions _options;
    private readonly SemaphoreSlim _discovery = new(1, 1);

    /// <summary>Creates the catalog over the plugin's settings.</summary>
    public AgentToolCatalog(AgentOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        BuiltIns = [.. BuiltInTools.Names.Select(n => new AgentBuiltInTool(n, BuiltInTools.Descriptions[n]))];
        Servers = [.. options.Servers.Values.OrderBy(s => s.Name, StringComparer.Ordinal).Select(Describe)];
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentBuiltInTool> BuiltIns { get; }

    /// <inheritdoc />
    public IReadOnlyList<McpServerInfo> Servers { get; }

    /// <inheritdoc />
    public async Task<McpDiscovery> DiscoverAsync(string server, CancellationToken cancellationToken)
    {
        if (server is null || !_options.Servers.TryGetValue(server, out var config))
        {
            return new McpDiscovery([], $"No MCP server named '{server}' is configured.", 0);
        }

        if (!config.Enabled)
        {
            return new McpDiscovery([], $"The MCP server '{server}' is disabled in the configuration; it was not started.", 0);
        }

        await _discovery.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await using var sessions = new McpSessions(_options);
            var tools = await sessions.ToolsAsync(server, cancellationToken).ConfigureAwait(false);
            var prefix = server + ".";
            var listed = tools
                .Select(t =>
                {
                    var name = t.Id.StartsWith(prefix, StringComparison.Ordinal) ? t.Id[prefix.Length..] : t.Id;
                    return new McpToolInfo(name, t.Description, JsonSerializer.Serialize(t.InputSchema), config.Allows(name), t.RequiresApproval);
                })
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList();
            return new McpDiscovery(listed, null, Elapsed(started));
        }
        catch (ToolFailureException ex)
        {
            return new McpDiscovery([], ex.Message, Elapsed(started));
        }
        finally
        {
            _discovery.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _discovery.Dispose();

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static McpServerInfo Describe(McpServerOptions s) => new(
        s.Name,
        s.Transport == McpTransport.Http ? "http" : "stdio",
        s.Transport == McpTransport.Http ? s.Url!.ToString() : string.Join(' ', [s.Command!, .. s.Arguments]),
        s.Enabled,
        [.. s.Tools.Order(StringComparer.Ordinal)],
        [.. s.Approval.Order(StringComparer.Ordinal)])
    {
        TokenVariable = s.TokenVariable,
        TokenSet = s.TokenVariable is not null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(s.TokenVariable)),
    };
}
