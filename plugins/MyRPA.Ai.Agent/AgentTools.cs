using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MyRPA.Core.Execution;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;

namespace MyRPA.Ai.Agent;

/// <summary>A tool failed; its message goes back to the model (redacted) and into the log summary.</summary>
public sealed class ToolFailureException : Exception
{
    /// <summary>Creates a failure.</summary>
    public ToolFailureException()
    {
    }

    /// <summary>Creates a failure with a message.</summary>
    /// <param name="message">What failed, without secrets.</param>
    public ToolFailureException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a failure with a message and a cause.</summary>
    /// <param name="message">What failed, without secrets.</param>
    /// <param name="innerException">The cause.</param>
    public ToolFailureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Whether it failed because it ran out of time.</summary>
    public bool TimedOut { get; init; }
}

/// <summary>
/// One tool an agent may call (ADR-0048): a stable id, a description, a JSON input schema, its source, whether it needs
/// approval, and an explicit implementation. Ids never resolve to types or methods; they are keys in a table built from
/// trusted configuration.
/// </summary>
public interface IAgentTool
{
    /// <summary>The id the model uses.</summary>
    string Id { get; }

    /// <summary>What it does.</summary>
    string Description { get; }

    /// <summary>The JSON schema its arguments must meet.</summary>
    JsonElement InputSchema { get; }

    /// <summary>Where it comes from: builtin, workflow or mcp.</summary>
    string Source { get; }

    /// <summary>Whether a person must approve each call (such calls are denied: there is no approval mechanism yet).</summary>
    bool RequiresApproval { get; }

    /// <summary>Why it may not run now (checked at every call); null when it may.</summary>
    /// <returns>The reason, or null.</returns>
    string? Unavailable();

    /// <summary>Runs it with arguments already checked against <see cref="InputSchema"/>.</summary>
    /// <param name="arguments">The arguments (a JSON object).</param>
    /// <param name="timeout">The tool timeout (sub-workflows are bounded by the engine with it).</param>
    /// <param name="cancellationToken">Cancels it (tool timeout, agent timeout or the run).</param>
    /// <returns>The result as JSON.</returns>
    /// <exception cref="ToolFailureException">It failed.</exception>
    Task<JsonNode?> InvokeAsync(JsonElement arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>A tool entry of the <c>tools</c> property, as the workflow author wrote it.</summary>
/// <param name="Kind">workflow, mcp or builtin.</param>
/// <param name="Target">The workflow path, the MCP server name or the built-in name.</param>
/// <param name="Name">The workflow tool's id, or the MCP tool name (<c>*</c> for all allowed).</param>
/// <param name="Description">The workflow tool's description.</param>
/// <param name="Input">The workflow tool's input: argument name → workflow type (<c>?</c> at the end: optional).</param>
internal sealed record ToolSpec(string Kind, string Target, string? Name, string? Description, IReadOnlyDictionary<string, string> Input);

/// <summary>Parses the <c>tools</c> property: a JSON array of workflow, mcp and builtin entries.</summary>
internal static class ToolSpecs
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1863:Use 'CompositeFormat'", Justification = "Error paths only.")]
    public static IReadOnlyList<ToolSpec> Parse(string text, AgentOptions options, Func<string, Exception> fail)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw fail($"'tools' is not JSON: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            throw fail("'tools' must be a JSON array of tool entries.");
        }

        var specs = new List<ToolSpec>();
        foreach (var entry in root.EnumerateArray())
        {
            string? Text(string name) => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim().Length > 0 ? v.GetString()!.Trim() : null;
            if (Text("workflow") is { } workflow)
            {
                var name = Text("name") ?? throw fail($"The workflow tool '{workflow}' needs a 'name' (letters, digits and _).");
                if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                {
                    throw fail($"The workflow tool name '{name}' may use only letters, digits and _.");
                }

                var input = new Dictionary<string, string>(StringComparer.Ordinal);
                if (entry.TryGetProperty("input", out var map))
                {
                    if (map.ValueKind != JsonValueKind.Object || map.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.String))
                    {
                        throw fail($"'input' of the workflow tool '{name}' must map argument names to types, such as {{ \"customer\": \"String\" }}.");
                    }

                    foreach (var property in map.EnumerateObject())
                    {
                        if (!WorkflowTool.TryType(property.Value.GetString()!, out _, out _))
                        {
                            throw fail($"The argument '{property.Name}' of the workflow tool '{name}' has an unknown type '{property.Value.GetString()}' (String, Int, Decimal, Boolean, DateTime, List, Dictionary, Object; '?' for optional).");
                        }

                        input[property.Name] = property.Value.GetString()!;
                    }
                }

                specs.Add(new ToolSpec("workflow", workflow, name, Text("description"), input));
            }
            else if (Text("mcp") is { } server)
            {
                if (!options.Servers.ContainsKey(server))
                {
                    throw fail($"There is no MCP server '{server}' in the AI Agent plugin's configuration.");
                }

                var tool = Text("tool") ?? throw fail($"The MCP entry of '{server}' needs a 'tool' (a tool name, or '*' for every tool the server allows).");
                specs.Add(new ToolSpec("mcp", server, tool, null, new Dictionary<string, string>()));
            }
            else if (Text("builtin") is { } builtin)
            {
                if (!BuiltInTools.Names.Contains(builtin))
                {
                    throw fail($"There is no built-in tool '{builtin}'; the built-ins are {string.Join(", ", BuiltInTools.Names)}.");
                }

                specs.Add(new ToolSpec("builtin", builtin, null, null, new Dictionary<string, string>()));
            }
            else
            {
                throw fail("Every tool entry needs 'workflow', 'mcp' or 'builtin'.");
            }
        }

        return specs;
    }
}

/// <summary>The tools of one agent run: built from trusted configuration, never from the model.</summary>
public sealed class ToolSet
{
    private readonly Dictionary<string, IAgentTool> _tools;
    private readonly HashSet<string> _knownElsewhere;

    private ToolSet(Dictionary<string, IAgentTool> tools, HashSet<string> knownElsewhere)
    {
        _tools = tools;
        _knownElsewhere = knownElsewhere;
    }

    /// <summary>How many tools the agent may use.</summary>
    public int Count => _tools.Count;

    /// <summary>The tools, in configuration order.</summary>
    public IEnumerable<IAgentTool> All => _tools.Values;

    /// <summary>The tool with this id, if the agent may use it.</summary>
    /// <param name="id">A tool id (untrusted: from the model).</param>
    /// <returns>The tool, or null.</returns>
    public IAgentTool? Find(string id) => _tools.GetValueOrDefault(id);

    /// <summary>Whether the id names a tool that exists but is not this agent's (a denial, not an unknown tool).</summary>
    /// <param name="id">A tool id (untrusted).</param>
    /// <returns>True for a known tool this agent may not use.</returns>
    public bool IsKnownElsewhere(string id) => _knownElsewhere.Contains(id);

    internal static async Task<ToolSet> BuildAsync(IReadOnlyList<ToolSpec> specs, IActivityContext context, McpSessions mcp, AgentActivity.AgentEvents events, Func<string, Exception> fail)
    {
        var tools = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        var known = new HashSet<string>(BuiltInTools.Names, StringComparer.Ordinal);
        void Add(IAgentTool tool)
        {
            if (!tools.TryAdd(tool.Id, tool))
            {
                throw fail($"the tool id '{tool.Id}' is listed twice.");
            }
        }

        foreach (var spec in specs)
        {
            switch (spec.Kind)
            {
                case "builtin":
                    Add(BuiltInTools.Create(spec.Target, context));
                    break;
                case "workflow":
                    Add(new WorkflowTool(spec, context));
                    break;
                default:
                    IReadOnlyList<IAgentTool> listed;
                    try
                    {
                        listed = await mcp.ToolsAsync(spec.Target, context.CancellationToken).ConfigureAwait(false);
                    }
                    catch (ToolFailureException ex)
                    {
                        throw new ActivityFailedException(AgentErrorTypes.ToolSourceUnavailable, $"{context.Node.Type} '{context.Node.Id}': {ex.Message}", ex);
                    }

                    known.UnionWith(listed.Select(t => t.Id));
                    var chosen = spec.Name == "*"
                        ? [.. listed.Where(t => t.Unavailable() is null)]
                        : listed.Where(t => t.Id == $"{spec.Target}.{spec.Name}").ToList();
                    if (chosen.Count == 0)
                    {
                        throw fail($"the MCP server '{spec.Target}' offers no tool '{spec.Name}'.");
                    }

                    chosen.ForEach(Add);
                    break;
            }
        }

        events.Info($"tools: {(tools.Count == 0 ? "none" : string.Join(", ", tools.Keys))}");
        return new ToolSet(tools, [.. known.Where(k => !tools.ContainsKey(k))]);
    }
}

/// <summary>
/// A project workflow as a tool: the author declares its id, description and typed input; it runs through the engine
/// (<see cref="IActivityContext.InvokeWorkflowAsync"/>), with every rule and side-effect control of its activities, and its
/// Out arguments are the result.
/// </summary>
internal sealed class WorkflowTool : IAgentTool
{
    private readonly ToolSpec _spec;
    private readonly IActivityContext _context;
    private readonly Dictionary<string, (WorkflowDataType Type, bool Required)> _input = new(StringComparer.Ordinal);

    public WorkflowTool(ToolSpec spec, IActivityContext context)
    {
        _spec = spec;
        _context = context;
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, typeText) in spec.Input)
        {
            TryType(typeText, out var type, out var optional);
            _input[name] = (type, !optional);
            properties[name] = Schema(type);
            if (!optional)
            {
                required.Add(name);
            }
        }

        using var schema = JsonDocument.Parse(new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false }.ToJsonString());
        InputSchema = schema.RootElement.Clone();
    }

    public string Id => _spec.Name!;

    public string Description => _spec.Description ?? $"Runs the workflow {_spec.Target}.";

    public JsonElement InputSchema { get; }

    public string Source => "workflow";

    public bool RequiresApproval => false;

    public string? Unavailable() => null;

    /// <summary>Reads a declared type: String, Int, Decimal, Boolean, DateTime, List, Dictionary or Object, with '?' for optional.</summary>
    public static bool TryType(string text, out WorkflowDataType type, out bool optional)
    {
        optional = text.EndsWith('?');
        return Enum.TryParse(optional ? text[..^1] : text, ignoreCase: false, out type) && Enum.IsDefined(type);
    }

    public async Task<JsonNode?> InvokeAsync(JsonElement arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!WorkflowValues.TryFromJson(property.Value, _input[property.Name].Type, out var value, out var error))
            {
                throw new ToolFailureException($"argument '{property.Name}': {error}");
            }

            values[property.Name] = value;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = await _context.InvokeWorkflowAsync(_spec.Target, values, timeout).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new ToolFailureException($"the workflow {_spec.Target} ended {result.Status}: {result.Error?.Code} {result.Error?.Message}".Trim())
            {
                TimedOut = result.Status == ExecutionStatus.TimedOut,
            };
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            WorkflowValues.WriteJson(writer, WorkflowValues.Dictionary(result.Outputs));
        }

        return JsonNode.Parse(buffer.ToArray());
    }

    private static JsonObject Schema(WorkflowDataType type) => type switch
    {
        WorkflowDataType.String => new JsonObject { ["type"] = "string" },
        WorkflowDataType.Int => new JsonObject { ["type"] = "integer" },
        WorkflowDataType.Decimal => new JsonObject { ["type"] = "number" },
        WorkflowDataType.Boolean => new JsonObject { ["type"] = "boolean" },
        WorkflowDataType.DateTime => new JsonObject { ["type"] = "string", ["description"] = "ISO 8601 date and time" },
        WorkflowDataType.List => new JsonObject { ["type"] = "array" },
        WorkflowDataType.Dictionary => new JsonObject { ["type"] = "object" },
        _ => [],
    };
}

/// <summary>The safe built-in tools: text, JSON and dates only; no files, network or processes.</summary>
internal static class BuiltInTools
{
    public static readonly IReadOnlyList<string> Names = ["time_now", "regex_extract", "json_get"];

    public static IAgentTool Create(string name, IActivityContext context) => name switch
    {
        "time_now" => new BuiltIn(name, "The current date and time (UTC, ISO 8601).", """{"type":"object","properties":{},"additionalProperties":false}""", _ =>
            new JsonObject { ["utc"] = context.TimeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) }),
        "regex_extract" => new BuiltIn(name, "Finds the matches of a .NET regular expression in a text (at most 50; 1 s timeout).", """{"type":"object","properties":{"text":{"type":"string","maxLength":200000},"pattern":{"type":"string","maxLength":500}},"required":["text","pattern"],"additionalProperties":false}""", RegexExtract),
        _ => new BuiltIn(name, "Reads a value from JSON text by a path such as items[0].name.", """{"type":"object","properties":{"json":{"type":"string","maxLength":1000000},"path":{"type":"string","maxLength":500}},"required":["json","path"],"additionalProperties":false}""", JsonGet),
    };

    private static JsonObject RegexExtract(JsonElement arguments)
    {
        try
        {
            var regex = new Regex(arguments.GetProperty("pattern").GetString()!, RegexOptions.None, TimeSpan.FromSeconds(1));
            var matches = new JsonArray();
            foreach (Match match in regex.Matches(arguments.GetProperty("text").GetString()!).Take(50))
            {
                var groups = new JsonObject();
                foreach (var group in match.Groups.Values.Skip(1).Where(g => g.Success))
                {
                    groups[group.Name] = group.Value;
                }

                matches.Add(new JsonObject { ["value"] = match.Value, ["groups"] = groups });
            }

            return new JsonObject { ["matches"] = matches };
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            throw new ToolFailureException($"regex_extract: {ex.Message}");
        }
    }

    private static JsonObject JsonGet(JsonElement arguments)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(arguments.GetProperty("json").GetString()!);
        }
        catch (JsonException ex)
        {
            throw new ToolFailureException($"json_get: the json is not valid: {ex.Message}");
        }

        foreach (Match part in PathPart().Matches(arguments.GetProperty("path").GetString()!))
        {
            node = part.Groups["name"].Success ? (node as JsonObject)?[part.Groups["name"].Value]
                : node is JsonArray array && int.Parse(part.Groups["index"].Value, CultureInfo.InvariantCulture) is var i && i < array.Count ? array[i]
                : null;
        }

        return new JsonObject { ["value"] = node?.DeepClone() };
    }

    private static Regex PathPart() => new(@"(?<name>[^.\[\]]+)|\[(?<index>\d+)\]", RegexOptions.None, TimeSpan.FromSeconds(1));

    private sealed class BuiltIn(string id, string description, string schema, Func<JsonElement, JsonNode?> run) : IAgentTool
    {
        public string Id { get; } = id;

        public string Description { get; } = description;

        public JsonElement InputSchema { get; } = JsonDocument.Parse(schema).RootElement.Clone();

        public string Source => "builtin";

        public bool RequiresApproval => false;

        public string? Unavailable() => null;

        public Task<JsonNode?> InvokeAsync(JsonElement arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(run(arguments));
        }
    }
}
