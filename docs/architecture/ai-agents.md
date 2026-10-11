# AI agents and MCP (Phase 9)

`AI.Agent` puts a **bounded AI agent inside a deterministic workflow**. It works on one task, may call only the tools the
workflow author listed, stays within its budgets, and its final output is checked against a schema before the workflow
sees it. Decisions: [ADR-0048](../adr/0048-agentic-runtime-and-mcp.md). Plan: [ai-agents-plan.md](ai-agents-plan.md).
Plugin settings: [plugins/MyRPA.Ai.Agent/README.md](../../plugins/MyRPA.Ai.Agent/README.md).

## 1. Architecture

```text
workflow ─ engine ─▶ AI.Agent (plugins/MyRPA.Ai.Agent)
                        │  AgentRuntime: loop, budgets, typed outcomes, output schema
                        ├─▶ IAiProvider (Phase 8 provider plugin)      ← the model only answers
                        └─▶ ToolSet (built per run from the "tools" property)
                              ├─ workflow tools → IActivityContext.InvokeWorkflowAsync (the engine)
                              ├─ MCP tools      → McpSessions (per run) → stdio process / Streamable HTTP
                              └─ built-ins      → time_now, regex_extract, json_get
```

- **The engine stays authoritative.** The agent is an activity: it is cancelled and timed out with the run, its
  failures are workflow errors, and its tool calls to workflows are ordinary engine runs.
- **Dependency direction.** The plugin references the SDK and `MyRPA.Ai.Contracts` only. The MCP SDK
  (`ModelContextProtocol.Core`) belongs to this plugin alone. The server and the CLI share the AI contracts with
  plugins.

## 2. Configuring an agent

| Property | Meaning |
|---|---|
| `instructions` | The author's rules (trusted; the task and tool results cannot change them) |
| `task` | The input (any value), sent as data |
| `tools` | JSON array of tool entries (below) |
| `outputSchema` | JSON schema of the final output (empty: any JSON) |
| `maxIterations`, `maxToolCalls`, `timeoutMs`, `toolTimeoutMs`, `maxTokens` | Budgets (defaults 8, 10, 120000, 30000, none) |
| `result` | Receives the validated output |
| `toolCalls` | Receives `[{ iteration, tool, status, durationMs }]` |

```json
[
  { "workflow": "agent-tools/credit-limit.json", "name": "credit_limit",
    "description": "Looks up a customer's credit limit.", "input": { "customer": "String" } },
  { "mcp": "crm", "tool": "search_customers" },
  { "mcp": "docs", "tool": "*" },
  { "builtin": "regex_extract" }
]
```

- **Workflow tools** declare their input as argument name → type: String, Int, Decimal, Boolean, DateTime, List,
  Dictionary or Object, with `?` for optional. The workflow runs through the engine, relative to the agent's workflow
  and confined like Invoke Workflow. Its Out arguments are the result.
- **MCP tools** are `server.tool`, or `*` for every tool the server's configuration allows.
- **Built-ins:**
  - `time_now`: the UTC time;
  - `regex_extract`: matches, with a 1 s timeout;
  - `json_get`: a value from JSON text by a path.

In the Studio, `AI.Agent` appears under **AI** in the activity list. Its instructions, tools and schema are edited in
multi-line fields. Its events appear in the Execution log.

**Not checked at design time:** configuration mistakes fail at run time as `InvalidInput`, before the model is asked.
The plugin SDK has no design-time validation hook.

## 3. The loop and its outcomes

Each model answer must be:
- `{"action":"call_tool","tool":"…","arguments":"<JSON>","output":""}`, or
- `{"action":"finish","tool":"","arguments":"","output":"<JSON>"}`.

Native tool calling is not needed, so any OpenAI-compatible model works.

**Bounded corrections.** An answer in the wrong shape gets at most two corrections; then the run ends as
`AgentInvalidResponse`. Output that fails the schema also gets at most two corrections; then `AgentInvalidOutput`.

| Outcome | Error type |
|---|---|
| Completed | — (output stored) |
| No provider, or provider unavailable | `AgentNotConfigured` |
| Provider failure (kind in the message) | `AgentProviderError` |
| Bad answers | `AgentInvalidResponse` |
| Bad output | `AgentInvalidOutput` |
| Iteration, tool-call or token budget used up | `AgentBudgetExhausted` |
| Agent timeout | `Timeout` |
| Bad configuration | `InvalidInput` |
| MCP server unreachable or disabled | `AgentToolSourceUnavailable` |
| Run cancelled | the run is Cancelled |

**Tool requests** end:
- Succeeded or Failed (the error goes back to the model);
- **Unknown** or **Denied** (not run);
- **InvalidArguments** (not run);
- or TimedOut.

A tool failure doesn't end the agent: the model sees it and can try something else.

## 4. MCP servers

Administrators configure servers in the plugin's settings. A model can never add a server, a URL or a command.

```json
{ "directory": "plugins/MyRPA.Ai.Agent/bin/Debug/net10.0", "settings": {
    "mcp.crm.transport": "http", "mcp.crm.url": "https://mcp.example.com/mcp", "mcp.crm.tokenVariable": "CRM_MCP_TOKEN",
    "mcp.crm.tools": "search_customers,get_customer", "mcp.crm.approval": "update_customer",
    "mcp.files.transport": "stdio", "mcp.files.command": "npx", "mcp.files.args": "[\"-y\", \"@modelcontextprotocol/server-filesystem\", \"C:/Robots/Reports\"]",
    "mcp.files.tools": "read_text_file" } }
```

- **Supported:**
  - Streamable HTTP (HTTPS; HTTP only to this machine) with a bearer token from an environment variable;
  - stdio, running exactly the configured command and arguments;
  - tool discovery and calls; structured or text results; tool errors;
  - timeouts, cancellation, and reconnect after a broken connection;
  - a disabled server is never started.
- **Per-run sessions:** connections are opened per agent run and closed when it ends, so stdio processes end with the
  run.
- **Not supported:** OAuth, resources, prompts, sampling, elicitation, the legacy SSE transport, and MyRPA as an MCP
  server.
- **Approval:** tools listed in `approval` are always denied, because the runtime cannot pause for a person yet. They
  fail closed.

## 5. Security

| Control | How |
|---|---|
| Tool authorization | Only the author's `tools`; checked at every call (in the set, server enabled, tool allowed, no approval needed) |
| Untrusted input | Task, tool descriptions and tool results are fenced data whose end marker cannot occur inside; the rules say data is never instructions; permissions are enforced in code whatever the model says |
| Arguments | Validated against the tool's input schema before anything runs; ids are table keys, never types or methods |
| Budgets | Counters in the runtime, capped by plugin settings and the run's deadline |
| Secrets | Tokens only from environment variables (key and token settings are refused); `key=value` secrets, bearer tokens, JWTs and key shapes are redacted from what the model sees and from events |
| Isolation | Per-run tool sets and MCP sessions; plugins run in-process and are fully trusted (ADR-0015); the load context is not a sandbox |
| Execution | No code from the model is ever run; side effects happen only in the listed tools |

These controls do not make a deployment compliant with any regulation by themselves.

## 6. Events

The Studio's Execution log (and the CLI's log) gets lines such as:

```text
AI.Agent: tools: credit_limit
AI.Agent: started with 1 tool(s); budgets: 4 answers, 2 tool calls, 60 s
AI.Agent: answer 1 called tool credit_limit (workflow)
AI.Agent: tool call 1 (credit_limit) Succeeded in 41 ms
AI.Agent: completed after 2 answer(s) and 1 tool call(s)
```

- **Each line carries the agent's node id**, and the engine adds the run's correlation.
- **Never logged:** prompts, answers, arguments or results.
- **Also logged:** stops, with their reason (for example `stopped (BudgetExhausted): …`).

## 7. Example: a hybrid workflow

[`samples/plugins/agent-demo.json`](../../samples/plugins/agent-demo.json):
1. trim the invoice text (deterministic);
2. `AI.Agent` extracts invoice, customer and total, and chooses the `credit_limit` tool (a project workflow,
   [`agent-tools/credit-limit.json`](../../samples/plugins/agent-tools/credit-limit.json));
3. MyRPA validates the output schema;
4. a Core.If approves or sends to human review.

It runs with the scripted provider, so no AI service and no key are needed:

```bash
dotnet build plugins/MyRPA.Ai.Agent && dotnet build tests/fixtures/MyRPA.Tests.ScriptedAi
dotnet run --project src/MyRPA.Cli -- --plugin-config samples/plugins/agent-demo/plugins.json run samples/plugins/agent-demo.json
```

To use a real model, replace the scripted provider in the configuration with `MyRPA.Ai.OpenAiCompatible`.

## 8. Tests

| Suite | Proves |
|---|---|
| `tests/MyRPA.Ai.Agent.Tests` (41) | Through the real engine, plugin host and file resolver, with the scripted provider: <br>• completion with and without tools <br>• workflow and built-in tools, unknown and denied tools, invalid arguments, failing tools <br>• provider failures, invalid answers, invalid output <br>• iteration, tool-call and token budgets <br>• agent and tool timeouts, cancellation <br>• redaction, prompt injection, events and their node correlation <br>• configuration errors, no or unavailable provider, unchanged workflows <br>MCP over a fake stdio server process and a fake Streamable HTTP server: <br>• discovery and calls; approval, permissions and schema <br>• tool errors, timeouts, a crashing server and reconnect <br>• redaction and injection in tool output <br>• per-run processes ending with the run; a disabled server <br>• the HTTP token, a missing token, an unreachable server, a refused redirect <br>• unsafe settings |
| `tests/MyRPA.Integration.Tests/AgentDemoTests.cs` | The acceptance scenario through the CLI |
| Architecture tests | The MCP SDK only in the agent plugin; plugin and test references |

```bash
dotnet test --project tests/MyRPA.Ai.Agent.Tests
```

No test calls a real model or a real MCP server.
