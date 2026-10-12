using System.Globalization;
using System.Text.Json;
using MyRPA.Ai.Contracts;
using MyRPA.Sdk.Plugins;

namespace MyRPA.Ai.Agent;

/// <summary>How an MCP server is reached (ADR-0048).</summary>
public enum McpTransport
{
    /// <summary>Streamable HTTP to a configured URL.</summary>
    Http = 0,

    /// <summary>A local process started from a configured command, speaking over standard input and output.</summary>
    Stdio = 1,
}

/// <summary>
/// An MCP server the administrator configured (plugin settings <c>mcp.&lt;name&gt;.*</c>). Agents can only use servers
/// listed here and only the tools listed in <see cref="Tools"/>; a model can never add a server, a URL or a command.
/// </summary>
/// <param name="Name">The server's name in tool ids (<c>name.tool</c>).</param>
/// <param name="Transport">How it is reached.</param>
/// <param name="Url">The Streamable HTTP endpoint (https, or http to this machine).</param>
/// <param name="TokenVariable">An environment variable holding a bearer token; null for none.</param>
/// <param name="Command">The executable of a stdio server.</param>
/// <param name="Arguments">Its arguments, exactly as configured.</param>
/// <param name="WorkingDirectory">Its working folder; null for the server process's own.</param>
/// <param name="Tools">The tools agents may use (<c>*</c> for all the server lists).</param>
/// <param name="Approval">Tools that need a person's approval: never run, because this runtime cannot pause for approval yet.</param>
/// <param name="Enabled">Whether the server may be used at all.</param>
/// <param name="ConnectTimeout">How long connecting and listing tools may take.</param>
public sealed record McpServerOptions(
    string Name,
    McpTransport Transport,
    Uri? Url,
    string? TokenVariable,
    string? Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlySet<string> Tools,
    IReadOnlySet<string> Approval,
    bool Enabled,
    TimeSpan ConnectTimeout)
{
    /// <summary>Whether the configuration lets agents use <paramref name="tool"/> of this server (checked again at every call).</summary>
    /// <param name="tool">The server's tool name.</param>
    /// <returns>True when enabled and listed.</returns>
    public bool Allows(string tool) => Enabled && (Tools.Contains("*") || Tools.Contains(tool));
}

/// <summary>The plugin's settings: MCP servers and the upper bounds of every agent's budgets.</summary>
/// <param name="Servers">The configured MCP servers by name.</param>
/// <param name="MaxIterations">The most model answers one agent may use.</param>
/// <param name="MaxToolCalls">The most tool calls one agent may make.</param>
/// <param name="MaxTimeout">The longest an agent may run.</param>
/// <param name="MaxToolResultChars">The longest tool result sent back to the model.</param>
public sealed record AgentOptions(
    IReadOnlyDictionary<string, McpServerOptions> Servers,
    int MaxIterations,
    int MaxToolCalls,
    TimeSpan MaxTimeout,
    int MaxToolResultChars);

/// <summary>Entry point of the AI Agent plugin (ADR-0048).</summary>
public sealed class AgentPlugin : IPlugin
{
    private AgentOptions? _options;

    /// <inheritdoc />
    public void Initialize(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings;
        var servers = new Dictionary<string, McpServerOptions>(StringComparer.Ordinal);
        var names = settings.Keys
            .Where(k => k.StartsWith("mcp.", StringComparison.Ordinal))
            .Select(k => k.Split('.'))
            .Where(p => p.Length == 3)
            .Select(p => p[1])
            .Distinct(StringComparer.Ordinal);
        foreach (var name in names)
        {
            servers[name] = Server(name, settings);
        }

        _options = new AgentOptions(
            servers,
            (int)Number(settings, "maxIterations", 50, 1, 500),
            (int)Number(settings, "maxToolCalls", 100, 0, 1000),
            TimeSpan.FromMilliseconds(Number(settings, "maxTimeoutMs", 600_000, 1_000, 3_600_000)),
            (int)Number(settings, "maxToolResultChars", 16_000, 256, 1_000_000));
    }

    /// <inheritdoc />
    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        registrar
            .AddInstance(_options ?? throw new InvalidOperationException("Initialize must run before Register."))
            .AddService<IAgentToolCatalog, AgentToolCatalog>(PluginServiceLifetime.Plugin)
            .AddActivity<AgentActivity>(AgentActivity.Descriptor);
    }

    private static McpServerOptions Server(string name, IReadOnlyDictionary<string, string> settings)
    {
        string? Text(string key) => settings.TryGetValue($"mcp.{name}.{key}", out var value) && value.Trim().Length > 0 ? value.Trim() : null;
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
        {
            throw new ArgumentException($"MCP server name '{name}' may use only letters, digits, '_' and '-'.", nameof(settings));
        }

        foreach (var forbidden in new[] { "token", "apiKey", "key", "password", "secret" })
        {
            if (settings.ContainsKey($"mcp.{name}.{forbidden}"))
            {
                throw new ArgumentException($"Setting 'mcp.{name}.{forbidden}' is not allowed: put the token in an environment variable and name it with 'mcp.{name}.tokenVariable'.", nameof(settings));
            }
        }

        var transport = Text("transport")?.ToUpperInvariant() switch
        {
            "HTTP" => McpTransport.Http,
            "STDIO" => McpTransport.Stdio,
            _ => throw new ArgumentException($"Setting 'mcp.{name}.transport' must be http or stdio.", nameof(settings)),
        };
        Uri? url = null;
        string? command = null;
        IReadOnlyList<string> arguments = [];
        if (transport == McpTransport.Http)
        {
            var text = Text("url") ?? throw new ArgumentException($"Setting 'mcp.{name}.url' is required for an http server.", nameof(settings));
            if (!Uri.TryCreate(text, UriKind.Absolute, out url)
                || !(url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback))
                || url.UserInfo.Length > 0 || url.Fragment.Length > 0)
            {
                throw new ArgumentException($"Setting 'mcp.{name}.url' must be an https address, or an http address of this machine, without credentials or fragment; '{text}' is not.", nameof(settings));
            }
        }
        else
        {
            command = Text("command") ?? throw new ArgumentException($"Setting 'mcp.{name}.command' is required for a stdio server: the executable an administrator chose.", nameof(settings));
            if (Text("args") is { } args)
            {
                try
                {
                    arguments = JsonSerializer.Deserialize<string[]>(args) ?? [];
                }
                catch (JsonException ex)
                {
                    throw new ArgumentException($"Setting 'mcp.{name}.args' must be a JSON array of strings, such as [\"server.py\", \"--readonly\"].", nameof(settings), ex);
                }
            }
        }

        var tokenVariable = Text("tokenVariable");
        if (tokenVariable is not null && !tokenVariable.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            throw new ArgumentException($"Setting 'mcp.{name}.tokenVariable' must be the name of an environment variable.", nameof(settings));
        }

        HashSet<string> List(string key) => [.. (Text(key) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        var tools = List("tools");
        if (tools.Count == 0)
        {
            throw new ArgumentException($"Setting 'mcp.{name}.tools' must list the tools agents may use (comma-separated), or '*' for all.", nameof(settings));
        }

        return new McpServerOptions(
            name,
            transport,
            url,
            tokenVariable,
            command,
            arguments,
            Text("workingDirectory"),
            tools,
            List("approval"),
            !string.Equals(Text("enabled"), "false", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromMilliseconds(Number(settings, $"mcp.{name}.timeoutMs", 30_000, 100, 300_000)));
    }

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
