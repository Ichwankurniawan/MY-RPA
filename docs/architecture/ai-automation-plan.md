# Phase 8: AI Workflow Assistant (plan)

Authorized by the owner on 2026-10-11. The requirements are the owner's brief,
[ai-workflow-assistant-brief.md](ai-workflow-assistant-brief.md), which replaced an earlier draft ("replace your draft
plan with mine, and proceed phase 8"). Decisions: [ADR-0047](../adr/0047-ai-provider-and-studio-assistant.md).

## Owner choices (2026-10-11)

| Question | Choice |
|---|---|
| First provider | An **OpenAI-compatible endpoint**: OpenAI, Azure OpenAI and local servers (Ollama, LM Studio). More providers come later behind `IAiProvider`. |
| API key | In the **server's configuration or environment**, never in the browser or a workflow file |
| Workflow content sent to the provider | Only when the user asks; the Studio shows what is sent |

## What the repository already provides (brief §2)

Phases 1–7 are complete (Phase 7 report, 2026-10-11).

| Brief needs | Existing contract |
|---|---|
| Workflow format | v1.0 JSON (sequences) and 1.1 (flowcharts and state machines, ADR-0037); `WorkflowLoader` with structured diagnostics |
| Activity metadata | Catalog 1.2 (`ActivityCatalogJson`): type, description, category, property kinds (Expression, Text, AssignmentTarget = outputs), required, value types, defaults, allowed values, child slots, side effects, secret properties |
| Validation | `WorkflowLoader` (parse → schema version → structure → semantics, expression whitelist), the source of truth |
| Studio | Web Studio (ADR-0028); pure edits and snapshot undo/redo (ADR-0029); ETags and dirty tracking |
| Execution diagnostics | Run result error (code, message, node, activity type, error type) and run events, which carry no workflow data (ADR-0023) |
| Secrets | Secret properties hold names, never literals (MYRPA1066) |

**Missing and built in Phase 8:**
- the provider boundary and providers;
- the generation pipeline, clarification and repair;
- a proposal diff;
- the assistant UI;
- AI lifecycle logging.

## Architecture

```text
Studio (Assistant tab) ──/api/assistant/*──▶ MyRPA.Server (endpoints, session, anti-forgery)
                                                 │
                                                 ▼
                         src/MyRPA.Ai.Assistant (pipeline: prompts, redaction, parse, policy, WorkflowLoader, repair)
                                                 │ IAiProvider
                                                 ▼
                         src/MyRPA.Ai.Contracts (BCL only) ◀── plugins/MyRPA.Ai.OpenAiCompatible ──HTTPS──▶ endpoint
```

- **`MyRPA.Ai.Contracts`**: BCL only, no references. It holds the provider interface and its error kinds.
- **`MyRPA.Ai.Assistant`** references Core, Workflow and Ai.Contracts. It contains the whole pipeline and nothing about
  HTTP, so it can be tested with a fake provider.
- **The server** hosts the endpoints.
- **Providers are plugins**, registered as extension contracts (ADR-0039).

## Slices

Status (2026-10-11): A-1 to A-6 done. Architecture and use: [ai-workflow-assistant.md](ai-workflow-assistant.md).

| Slice | Delivers |
|---|---|
| **A-1** | Contracts (capabilities, failure kinds); the OpenAI-compatible plugin (bounded retries, timeouts, no redirects, HTTPS except loopback, key from the environment); a scripted fake provider fixture; `GET /api/assistant`; architecture rules |
| **A-2** | The pipeline in `MyRPA.Ai.Assistant`: <br>• catalog metadata (no `Code.*`, brief §4.5) <br>• redaction <br>• one structured answer: clarify, unsupported or proposal <br>• deterministic checks: size, JSON, schema version, `WorkflowLoader`, catalog policy, edit identity <br>• bounded repair <br>• explain, validation-fix and failure-debug requests <br>• logging <br>Server endpoints: `generate`, `explain`, `debug`, `preview` |
| **A-3** | Studio Assistant tab: request, clarifying questions, plan, proposal review (facts, side effects, diff), Create as new file, cancel, failure messages |
| **A-4** | Controlled edits of the open workflow: tied to the document revision, stale check, diff by node id, applied as one undo step |
| **A-5** | Explain this workflow; fix validation problems; explain a failed run and propose a validated fix |
| **A-6** | Security tests (prompt injection, redaction, no run after generation), docs, end to end, and the final report (brief §20), then stop |
