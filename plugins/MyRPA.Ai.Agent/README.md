# MyRPA.Ai.Agent

The `AI.Agent` activity ([ADR-0048](../../docs/adr/0048-agentic-runtime-and-mcp.md), guide:
[ai-agents.md](../../docs/architecture/ai-agents.md)). It is a bounded AI agent inside a workflow:
- it works on one task with the tools the workflow author lists (project workflows, MCP server tools, built-ins);
- it stays within budgets;
- its output is checked against a schema.

It needs an AI provider plugin (such as [MyRPA.Ai.OpenAiCompatible](../MyRPA.Ai.OpenAiCompatible/README.md)).

The plugin owns the MCP client SDK (`ModelContextProtocol.Core`, which no other project may reference). It runs
in-process and is fully trusted (ADR-0015); its load context is not a sandbox. What protects a run is the agent's own
code: tool authorization, schema checks, budgets and redaction.

## Settings (plugin configuration, ADR-0019)

| Setting | Default | Meaning |
|---|---|---|
| `maxIterations` | 50 | The most answers any agent may ask for |
| `maxToolCalls` | 100 | The most tool calls any agent may ask for |
| `maxTimeoutMs` | 600000 | The longest timeout any agent may ask for |
| `maxToolResultChars` | 16000 | The longest tool result sent to the model |
| `mcp.<name>.transport` | required | `http` or `stdio` |
| `mcp.<name>.url` | required for http | The Streamable HTTP endpoint: https, or http only to this machine; redirects are refused |
| `mcp.<name>.tokenVariable` | none | An environment variable holding a bearer token (a `token`, `key`, `apiKey`, `password` or `secret` setting is refused) |
| `mcp.<name>.command`, `mcp.<name>.args` | required for stdio | The executable and its arguments (a JSON array), exactly as run |
| `mcp.<name>.workingDirectory` | none | The stdio server's working folder |
| `mcp.<name>.tools` | required | The tools agents may use (comma-separated, or `*`) |
| `mcp.<name>.approval` | none | Tools that need a person's approval: always denied for now |
| `mcp.<name>.enabled` | true | `false` keeps the server unused |
| `mcp.<name>.timeoutMs` | 30000 | Connecting and listing tools |

The activity's properties, tool entries, outcomes, error types and events are described in
[ai-agents.md](../../docs/architecture/ai-agents.md).

## Tests

`tests/MyRPA.Ai.Agent.Tests` runs agents through the real engine and plugin host, using:
- the scripted provider (`tests/fixtures/MyRPA.Tests.ScriptedAi`);
- a fake stdio MCP server (`tests/fixtures/MyRPA.Tests.FakeMcpServer`);
- a fake Streamable HTTP MCP server.

No test calls a real model or MCP server.
