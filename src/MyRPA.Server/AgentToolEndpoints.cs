using System.Text.Json;
using MyRPA.Ai.Contracts;

namespace MyRPA.Server;

/// <summary>
/// <c>/api/agent-tools</c> (ADR-0048 amendment): the tools an AI agent can be given besides project workflows, from the
/// agent plugin's <see cref="IAgentToolCatalog"/>. Read-only: servers are configured in the plugin's settings, never
/// here. Discovery connects to a configured server (a stdio server's configured command runs for the call).
/// </summary>
internal static class AgentToolEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/agent-tools", (IEnumerable<IAgentToolCatalog> catalogs) =>
        {
            var catalog = catalogs.FirstOrDefault();
            return Results.Json(new
            {
                available = catalog is not null,
                reason = catalog is null ? "The AI Agent plugin (MyRPA.Ai.Agent) is not loaded: add it to the server's plugin configuration." : null,
                builtIns = catalog?.BuiltIns.Select(b => new { name = b.Name, description = b.Description }) ?? [],
                servers = catalog?.Servers.Select(s => new
                {
                    name = s.Name,
                    transport = s.Transport,
                    endpoint = s.Endpoint,
                    enabled = s.Enabled,
                    allowedTools = s.AllowedTools,
                    approvalTools = s.ApprovalTools,
                    tokenVariable = s.TokenVariable,
                    tokenSet = s.TokenSet,
                }) ?? [],
            });
        });

        api.MapPost("/agent-tools/mcp/{name}/discover", async (string name, IEnumerable<IAgentToolCatalog> catalogs, HttpContext context) =>
        {
            if (catalogs.FirstOrDefault() is not { } catalog)
            {
                return Results.Json(new { error = "The AI Agent plugin is not loaded." }, statusCode: StatusCodes.Status404NotFound);
            }

            if (!catalog.Servers.Any(s => s.Name == name))
            {
                return Results.Json(new { error = $"No MCP server named '{name}' is configured." }, statusCode: StatusCodes.Status404NotFound);
            }

            var discovery = await catalog.DiscoverAsync(name, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(new
            {
                ok = discovery.Error is null,
                error = discovery.Error,
                durationMs = discovery.DurationMs,
                tools = discovery.Tools.Select(t => new
                {
                    name = t.Name,
                    description = t.Description,
                    inputSchema = Schema(t.InputSchema),
                    allowed = t.Allowed,
                    requiresApproval = t.RequiresApproval,
                }),
            });
        });
    }

    private static JsonElement? Schema(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
