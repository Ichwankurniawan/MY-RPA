namespace MyRPA.Ai.Contracts;

/// <summary>
/// What an agent plugin can offer as tools besides project workflows (ADR-0048 amendment): its built-in tools and the MCP
/// servers an administrator configured. The host shows it to people choosing an agent's tools; it is read-only, and
/// nothing here lets a model, a workflow or the Studio add a server, a URL or a command.
/// </summary>
public interface IAgentToolCatalog
{
    /// <summary>The built-in tools.</summary>
    IReadOnlyList<AgentBuiltInTool> BuiltIns { get; }

    /// <summary>The configured MCP servers, ordered by name.</summary>
    IReadOnlyList<McpServerInfo> Servers { get; }

    /// <summary>
    /// Connects to a configured server and lists its tools (a connection test): a stdio server's configured command
    /// runs for the length of the call. A disabled server is never started.
    /// </summary>
    /// <param name="server">The server's name.</param>
    /// <param name="cancellationToken">Cancels the discovery.</param>
    /// <returns>The tools, or why they could not be listed.</returns>
    Task<McpDiscovery> DiscoverAsync(string server, CancellationToken cancellationToken);
}

/// <summary>A built-in agent tool.</summary>
/// <param name="Name">Its name in an agent's <c>tools</c> (<c>{ "builtin": name }</c>).</param>
/// <param name="Description">What it does.</param>
public sealed record AgentBuiltInTool(string Name, string Description);

/// <summary>An MCP server as configured (never its token).</summary>
/// <param name="Name">Its name in an agent's <c>tools</c> (<c>{ "mcp": name, "tool": … }</c>).</param>
/// <param name="Transport"><c>http</c> or <c>stdio</c>.</param>
/// <param name="Endpoint">The URL of an http server, or the command and arguments of a stdio server.</param>
/// <param name="Enabled">Whether agents may use it.</param>
/// <param name="AllowedTools">The tools agents may use (<c>*</c>: all the server lists).</param>
/// <param name="ApprovalTools">Tools that need a person's approval (always refused until approval exists).</param>
public sealed record McpServerInfo(string Name, string Transport, string Endpoint, bool Enabled, IReadOnlyList<string> AllowedTools, IReadOnlyList<string> ApprovalTools)
{
    /// <summary>The environment variable holding the bearer token; null for none.</summary>
    public string? TokenVariable { get; init; }

    /// <summary>Whether that variable is set for this process (its value is never exposed).</summary>
    public bool TokenSet { get; init; }
}

/// <summary>A tool an MCP server listed.</summary>
/// <param name="Name">The server's tool name.</param>
/// <param name="Description">The server's description (untrusted text).</param>
/// <param name="InputSchema">The server's input schema (JSON text, untrusted).</param>
/// <param name="Allowed">Whether the configuration lets agents use it.</param>
/// <param name="RequiresApproval">Whether it needs approval (and is therefore refused).</param>
public sealed record McpToolInfo(string Name, string Description, string InputSchema, bool Allowed, bool RequiresApproval);

/// <summary>The outcome of listing a server's tools.</summary>
/// <param name="Tools">The tools, ordered by name (empty on failure).</param>
/// <param name="Error">Why listing failed; null on success.</param>
/// <param name="DurationMs">How long it took.</param>
public sealed record McpDiscovery(IReadOnlyList<McpToolInfo> Tools, string? Error, long DurationMs);
