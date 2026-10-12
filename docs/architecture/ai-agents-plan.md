# Phase 9: AI Agents & MCP (plan)

Brief: the owner's Phase 9 brief ("Agentic Automation Runtime & MCP"), pasted 2026-10-12 and confirmed with "Yes,
proceed to Phase 9". Decisions: [ADR-0048](../adr/0048-agentic-runtime-and-mcp.md). Guide: [ai-agents.md](ai-agents.md).

## Owner choices (2026-10-12)

| Question | Choice |
|---|---|
| Tools | Project workflows, MCP server tools and safe built-ins; no raw activities |
| Tool calling | A JSON action protocol on Phase 8's structured output; no `IAiProvider` change |
| MCP transports | Streamable HTTP and stdio (official `ModelContextProtocol.Core` SDK, in a plugin) |

## Inspection (brief §2)

**Reused:**
- `IAiProvider` and the providers (ADR-0047);
- the scripted provider fixture;
- the activity contract and catalog;
- `IActivityContext.InvokeWorkflowAsync`;
- `WorkflowValues` JSON conversion;
- the execution log (workflow log category);
- the plugin host's extension contracts;
- Phase 7's multiline catalog editing in the Studio.

**Missing, and built here:**
- the agent runtime;
- the tool layer;
- the MCP client;
- the schema checker.

**Constraints:**
- No AI or MCP SDK in `src`.
- Plugins cannot see a workflow's location, so workflow tools declare their input.
- The plugin SDK has no design-time validation hook.

## Slices (all done 2026-10-12)

| Slice | Delivered |
|---|---|
| G-1 | `plugins/MyRPA.Ai.Agent`: the runtime, typed outcomes, budgets, the JSON action protocol, output validation |
| G-2 | The tool layer: workflow, MCP and built-in tools; checks at call time; redaction; events |
| G-3 | The MCP client (Streamable HTTP and stdio) with per-run sessions and cleanup |
| G-4 | The CLI references the AI contracts; the runnable acceptance demo (`samples/plugins/agent-demo.json`) |
| G-5 | Tests: 41 agent and MCP tests (fake provider, fake stdio and HTTP servers), one CLI acceptance test, architecture rules; docs |

## Not in this phase

Orchestrator, scheduling, robots, a credential vault, MyRPA as an MCP server, human approval with pause and resume,
native tool calling, and a Studio tool picker. These are future work, needing the owner's authorization.
