# ADR-0048: Agentic automation runtime, the AI.Agent activity, tools and MCP

- Status: **Accepted** (owner, 2026-10-12).
  - The Phase 9 brief was pasted; the owner then confirmed "Yes, proceed to Phase 9".
  - Choices: tools are project workflows, MCP server tools and safe built-ins; the JSON action protocol; MCP over
    Streamable HTTP and stdio.
- Phase: 9 (AI Agents & MCP). Guide: [ai-agents.md](../architecture/ai-agents.md). Plan:
  [ai-agents-plan.md](../architecture/ai-agents-plan.md).
- Builds on:
  - ADR-0047 (`IAiProvider`, providers as plugins);
  - ADR-0013 (the activity contract);
  - ADR-0039 (extension contracts);
  - ADR-0012 and ADR-0027 (workflow invocation and its confinement).
- Amends:
  - ADR-0042: the agent plugin may reach its configured MCP endpoints and start configured MCP server processes;
  - `ArchitectureRules`: `ModelContextProtocol.Core` belongs to `MyRPA.Ai.Agent`; the CLI references the AI contracts.

## Context

The brief asks for agents inside deterministic workflows:
- a bounded loop between a model and authorized tools;
- typed outcomes, budgets, validated structured output and observable events;
- a common tool layer and an MCP client;
- no second execution system and no unrestricted autonomy.

The architecture forbids AI and MCP SDKs in `src`. Plugins reach the engine only through the frozen SDK
(`IActivityContext`), which can invoke workflows but does not reveal a workflow's location. Phase 8's provider contract
has structured (JSON-schema) output but no native tool calling.

## Decision

1. **`AI.Agent` is an ordinary activity** of a new plugin, `plugins/MyRPA.Ai.Agent`.
   - It is discoverable in the catalog and configured through its properties (no new schema; workflow format 1.0 is
     unchanged).
   - It runs inside the engine like any activity.
   - **Its model** is the first `IAiProvider` a plugin registered (Phase 8). No provider: `AgentNotConfigured`.
2. **The loop uses a JSON action protocol** on Phase 8's structured output, so no change to `IAiProvider` is needed.
   - **Each answer** is `{action: call_tool | finish, tool, arguments, output}`, validated in code.
   - **Invalid answers** and **invalid output** each get at most two corrections.
   - **No native tool calling, and nothing a model writes is evaluated.**
3. **Typed outcomes.**
   - **The run ends** Completed, ProviderError, InvalidResponse, InvalidOutput, BudgetExhausted, TimedOut or
     Cancelled.
   - **Each tool request ends** Succeeded, Failed, Unknown, Denied, InvalidArguments or TimedOut.
   - **Failures map to error types:** `AgentNotConfigured`, `AgentProviderError`, `AgentInvalidResponse`,
     `AgentInvalidOutput`, `AgentBudgetExhausted`, `Timeout`, `InvalidInput`, `AgentToolSourceUnavailable`.
     Workflows can catch them with TryCatch.
   - **Cancellation** propagates.
4. **Tools are declared by the workflow author** in the `tools` property (trusted configuration), never by the model.
   - **Workflow tools:** a project workflow, with an id, a description and its typed input declared next to its use.
     The SDK does not expose workflow locations, so a workflow's arguments cannot be read before it runs. The engine
     still binds the arguments when it runs the workflow, through `InvokeWorkflowAsync` with its usual confinement and
     every control of its activities. Out arguments are the result.
   - **MCP tools:** `server.tool` from a configured server.
   - **Built-ins:** `time_now`, `regex_extract` and `json_get`. No files, network or processes.
   - **Raw activities are not exposed.** Anything else an agent should do is wrapped in a workflow, which reuses the
     activities and their security.
5. **Every call is checked in code, at call time**, before anything runs:
   - the tool is known and in this agent's set; otherwise Unknown, or Denied when it exists elsewhere;
   - the server is enabled and allows the tool;
   - approval is not required;
   - the arguments meet the input schema (a documented JSON-schema subset).

   Results go back as fenced data, redacted and shortened. **Tools that need approval are denied** (fail closed): the
   runtime cannot pause for a person yet.
6. **Budgets live in the runtime:** answers, tool calls, overall time (also capped by the run's deadline), time per
   tool call, and tokens when the provider reports usage. Plugin settings cap what a workflow may ask for. The model
   never sees or sets the counters.
7. **The MCP client** is the official SDK (`ModelContextProtocol.Core` 2.2.0), used only in this plugin.
   - **Servers** are configured by the administrator in plugin settings (`mcp.<name>.*`).
   - **Streamable HTTP:** HTTPS (HTTP only to loopback), no redirects, a bearer token from an environment variable.
   - **stdio:** exactly the configured command and arguments.
   - **Isolation:** connections are opened per agent run and closed when it ends, so no session, process or
     authorization state crosses runs. A broken connection is dropped and reopened on next use.
   - **Not supported:** OAuth, resources, prompts, sampling, the legacy SSE transport, and MyRPA as an MCP server.
8. **Observability uses the existing execution log.**
   - **Events:** started, tools, each call and its outcome with its duration, corrections, completion, and stops with
     their reason.
   - **Correlation:** each event carries the node id, and the run's correlation comes from the engine.
   - **Never logged:** prompts, answers, arguments or results.
   - **The `toolCalls` output** lists `{ iteration, tool, status, durationMs }`.

## Consequences

- A workflow can be deterministic around an agent: prepare → `AI.Agent` → validate/decide. The engine stays
  authoritative and every side effect happens in ordinary activities.
- **No design-time checks:** misconfiguration (bad `tools` JSON, unknown servers or built-ins, an invalid schema)
  fails at run time as `InvalidInput`, before the model is asked. The plugin SDK has no validation hook.
- **Coming later:**
  - native tool calling (a provider capability);
  - human approval with pause and resume;
  - ~~a tool picker in the Studio fed by a server endpoint~~ (done, see the amendment below);
  - MyRPA as an MCP server;
  - OAuth for MCP.

## Amendment (2026-10-12): agent tools in the Studio

The owner could not see the MCP architecture: servers lived only in plugin settings and an agent's `tools` was raw
JSON. Decisions:

- **`IAgentToolCatalog`** (in `MyRPA.Ai.Contracts`, additive) is registered by the agent plugin: its built-ins, its
  configured MCP servers (name, transport, URL or command, enabled, allowed and approval tools, the token's environment
  variable and whether it is set; never a token value) and `DiscoverAsync`, a connection test that opens a short-lived
  session (a stdio server's process ends with it), one at a time, never for a disabled server.
- **Server:** `GET /api/agent-tools` and `POST /api/agent-tools/mcp/{name}/discover` (session, Origin and anti-forgery
  as every Studio API); `/api/info` says `agentTools` when the plugin is loaded. Read-only: servers are still configured
  only in the plugin's settings; the Studio, a model or a workflow can never add a server, a URL or a command.
- **Catalog editor hint:** `ActivityPropertyDefinition.Editor` (catalog JSON `editor`, additive within 1.2 like
  `multiline`) names a richer editor for a text property; tools that do not know it use their ordinary editor. The
  agent's `tools` property declares `agentTools`. The Studio keeps no activity knowledge: it knows editor kinds, the
  catalog says which property uses one.
- **Studio:** an **Agent tools** rail page (only when the plugin is loaded) lists MCP servers with **Test connection**
  and the built-ins; under `tools`, a picker adds MCP server tools (from the configuration, or from the server after
  **Load tools from server**), built-ins and project workflows (their In arguments become `input`), and removes entries.
  It writes the same JSON the text box shows; the text stays editable, and text the picker cannot read is left to it.
- **Not done:** editing server settings from the Studio (the configuration file and a Server restart remain the way),
  OAuth, MCP resources and prompts, and approval.
