# AI Workflow Assistant (Phase 8)

The Studio's AI Workflow Assistant turns a description into a proposed workflow in the existing format, asks when
something is unclear, explains workflows and failed runs, and proposes corrections. It is a **development assistant**:
it never runs, saves or applies anything by itself. Decisions: [ADR-0047](../adr/0047-ai-provider-and-studio-assistant.md).
Requirements: [the owner's brief](ai-workflow-assistant-brief.md). Plan and slices: [ai-automation-plan.md](ai-automation-plan.md).

## 1. Architecture

```text
Studio: Assistant tab ─── /api/assistant/* ───▶ MyRPA.Server (session, Origin, anti-forgery; AssistantHost)
                                                   │
                                                   ▼
                   src/MyRPA.Ai.Assistant: WorkflowAssistant (prompts, redaction, checks, WorkflowLoader, bounded repair)
                                                   │ IAiProvider (src/MyRPA.Ai.Contracts, BCL only, extension contract)
                                                   ▼
                   plugins/MyRPA.Ai.OpenAiCompatible ── HTTPS (or loopback HTTP) ──▶ the configured endpoint
```

| Part | Role |
|---|---|
| `MyRPA.Ai.Contracts` | `IAiProvider`, `AiRequest` (instructions, messages, JSON schema, output limit), `AiCompletion`, `AiProviderException` with `AiFailureKind` |
| `MyRPA.Ai.Assistant` | The pipeline; references Core, Workflow and the contracts only. No HTTP, no engine, no Studio code. |
| `plugins/MyRPA.Ai.OpenAiCompatible` | The first provider (OpenAI, Azure OpenAI, Ollama, LM Studio). See its [README](../../plugins/MyRPA.Ai.OpenAiCompatible/README.md). |
| `MyRPA.Server` (`Assistant.cs`) | `AssistantHost` (the first registered provider) and the endpoints below |
| `web/studio` (`AssistantPanel.tsx`, `assistant.ts`) | The Assistant tab, the review diff and the entry points in Problems and Execution |

**Activity metadata** comes only from the host's catalog (`ActivityCatalogJson`): type, description, category,
property kinds (Expression, Text, AssignmentTarget for outputs, LocalName), required, value types, defaults, allowed
values, slots and side effects. There is no separate activity list. The expression functions come from the
`ExpressionFunctions` whitelist. The `Code.*` scripting activities are left out and refused (brief §4.5).

## 2. Configuring a provider

The assistant is available only when the server loads an AI provider plugin. Without one, the Assistant tab says why
and nothing is ever sent. The key is never in a file: the plugin reads it from the environment variable named by
`apiKeyVariable` (default `OPENAI_API_KEY`).

```json
{ "pluginConfigVersion": "1.0", "plugins": [
  { "directory": "plugins/MyRPA.Ai.OpenAiCompatible/bin/Debug/net10.0",
    "settings": { "endpoint": "https://api.openai.com/v1", "model": "gpt-4.1" } } ] }
```

For a local model, no key is needed and workflow content stays on the machine:
`{ "endpoint": "http://localhost:11434/v1", "model": "llama3.1", "apiKeyVariable": "" }`. All settings are in the
plugin's README. A setting named `apiKey`, `key`, `token`, `password` or `secret` is refused.

## 3. Lifecycle of a request

1. **The person asks.** The Assistant tab's request box offers **New workflow** (create) and **Change this workflow**
   (edit). **Fix with the assistant** in Problems asks for a validation fix. **Ask the assistant why** on a failed run
   explains it, and its **Propose a fix** asks for a failure fix.
2. **The server builds the request** (`POST /api/assistant/generate`). The instructions (rules) are followed by fenced
   data blocks:
   - the catalog and the expression functions;
   - the current workflow (edits and fixes);
   - for a validation fix, the validator's diagnostics, computed by the server and never sent by the browser;
   - for a failure fix, the run's error, read by the server from its own run;
   - the conversation and the request.
3. **The model answers one structured object:** `clarify`, `unsupported` or `proposal`. Known facts, assumptions and
   questions are kept apart.
4. **Deterministic checks**, which never trust the model:
   - the answer size and JSON;
   - the schema version: the current one for a change; 1.0, or 1.1 for flowcharts, for a new workflow;
   - no `Code.*` activity;
   - the workflow id kept when editing;
   - then `WorkflowLoader`: types, properties, slots, value types, expressions.
5. **Bounded repair.** An invalid proposal is sent back with its diagnostics at most twice (`MaxRepairAttempts`). Each
   attempt is revalidated. If it is still invalid, the answer is `invalid` with the last proposal and its diagnostics,
   and it cannot be applied.
6. **Review** in the Studio:
   - the plan;
   - the assistant's own description, labelled as such;
   - the assumptions to check;
   - the activities the workflow **actually** uses and what they touch (files, the network, a browser; from the
     catalog);
   - for a change, a diff by node id (added, removed, changed properties, moved) and the workflow-level changes;
   - any warnings.
7. **Apply or discard.**
   - **A new workflow** becomes a new file (its name is asked) and opens.
   - **A change** replaces the open document as **one undo step**. It is refused if the document changed after the
     request (the stale check compares the document object).
   - Nothing is ever run: running stays the person's own action.

**Clarification:** questions come with short options when there are obvious ones. The person answers, and the
conversation (the original request and the questions) is sent with the reply. At most 20 turns.

**Explain** (`POST /api/assistant/explain`) returns a summary and notes on nodes. Notes for ids that are not in the
workflow are dropped, and each note selects its node. **Debug** (`POST /api/assistant/debug`) returns a likely,
possible or unknown cause, the node it points to, and suggestions. It never repeats the run.

## 4. Server API

| Endpoint | Meaning |
|---|---|
| `GET /api/assistant` | `{ available, provider, model, reason }` |
| `POST /api/assistant/preview` | `{ operation: generate\|explain\|debug, ... }` → `{ instructions, messages, schema }`: exactly what would be sent; nothing is sent |
| `POST /api/assistant/generate` | `{ purpose: create\|edit\|fixValidation\|fixFailure, request, document?, conversation?, runId? }` → `{ outcome, known, assumptions, questions, missing, plan, summary, document, diagnostics, repairAttempts, activityTypes, sideEffects, usage }` |
| `POST /api/assistant/explain` | `{ document }` → `{ summary, steps: [{ nodeId, text }], usage }` |
| `POST /api/assistant/debug` | `{ document, runId }` (a finished, failed run of this server) → `{ explanation, certainty, nodeId, suggestions, failure, usage }` |

**Errors:**
- 400 for a refused request (a bad purpose, a missing document, an empty request);
- 404 for an unknown run;
- 409 for a run that has not finished or did not fail;
- 503 `NotConfigured` without a provider;
- provider failures keep their kind: 429 `RateLimited`, 504 `Timeout`, 413 `TooLarge`, 502 for the others.

A provider failure is never a successful answer, and the document is unchanged.

## 5. Security boundaries

| Concern | Protection |
|---|---|
| Model output is untrusted | Every proposal passes the deterministic checks and `WorkflowLoader`; unknown activities are never substituted; `Code.*` is refused; invalid proposals cannot be applied |
| Prompt injection | Requests, replies, workflows, catalog text and error messages travel in fenced data blocks whose end marker cannot occur inside them; the instructions say data is never instructions; the checks above apply whatever the model was told |
| Secrets | Workflows hold secret names, not values (MYRPA1066). Before sending, string values and error messages are redacted: `key=value` secrets, bearer and basic credentials, JSON web tokens and common API-key shapes. The provider key stays in the server's environment and is never logged or returned. |
| Data sent | Only on a button press. What is sent: the workflow being edited, the catalog, the expression functions and, for a failure, the run's error (code, redacted and shortened message, node, activity type, error type). Never sent: run values (events carry none, ADR-0023), typed argument values, plugin settings, other workflows, files. **What is sent** shows the exact request. |
| Tools | The model has no tools: no file system, shell, network, plugin loading or service access, and no tool-calling loop. It only answers. |
| Execution | Generating, applying and fixing never run anything; a run is the person's own action through `ExecutionHost`. |
| Transport | HTTPS to the configured endpoint only (HTTP only to this machine); no redirects; TLS validation never off; the session, Origin and anti-forgery checks guard every assistant request. |

Provider data retention is the provider's policy. Choose a local model, or a provider whose terms fit, when workflows
contain sensitive business information.

## 6. Logging and privacy

Each operation logs one line (`EventId` 4700 on success, 4701 on failure) with:
- the operation, the outcome or failure kind, and the correlation id (the HTTP request's trace id);
- the provider and model, the number of requests, the duration, the tokens in and out, and the repair attempts.

Prompts, answers and workflows are never logged.

## 7. Supported and unsupported

- **Supported:** sequences of the loaded activities and expressions, including control flow, error handling, and
  plugin activities (files, HTTP, Excel, email, database, SFTP, browser) when their plugins are loaded; changes to
  open workflows; explanations; validation and failure fixes.
- **Unsupported, reported as such:** anything no loaded activity can do. The assistant names the missing capability
  instead of inventing an activity, and never writes code as a workaround. Answers depend on the model; a weak model
  produces more clarifications, repairs and invalid proposals, but never an unchecked one.
- **Known limitations:**
  - changes are whole-document proposals with a node-id diff, not patches;
  - flowchart and state-machine layouts are not arranged by the assistant;
  - conversations are not stored;
  - only OpenAI-compatible endpoints have a provider plugin.

## 8. Tests (no model, no key)

| Suite | What it proves |
|---|---|
| `tests/MyRPA.Ai.Assistant.Tests` (fake provider, real built-in catalog) | Generation, clarification, unsupported; malformed JSON, unsupported schema, unknown activity, missing property, invalid nesting and expressions, oversized answers; bounded repair; edit identity; validation and failure fixes; provider failures and cancellation; redaction; prompt injection; logging without content |
| `tests/MyRPA.Ai.OpenAiCompatible.Tests` (local HTTP endpoint) | Request shape, key handling, Azure style, schema fallback, every failure kind, retries, timeout, cancellation, size limit, refused settings |
| `tests/MyRPA.Server.Tests/AssistantTests.cs` (scripted provider fixture) | Availability, preview without sending, generate without touching disk, provider failure kinds, debug from the run's own error, bad requests, anti-forgery |
| `web/studio` Vitest (`AssistantUi.test.tsx`, `assistant.test.ts`) | Unavailable state, create as new file without running, clarification with options, unsupported and invalid answers, apply as one undo step, stale refusal, failures, cancel, explain, fix from Problems, debug and fix from Execution, the review diff |
| `npm run smoke`, `npm run a11y` | A real server with the scripted provider fixture (`tests/fixtures/MyRPA.Tests.ScriptedAi`): describe, review, create; accessibility of the proposal |

The scripted fixture answers from a JSON file. It can also fail with any `AiFailureKind`, delay, or report itself as
unconfigured.

## 9. Phase 9

Phase 9 (AI agents and MCP) can build on:
- the provider contract;
- the bounded operations (list activities, read the workflow, propose, validate, compare, explain, summarize a
  failure);
- the deterministic checks.

Tools, an agent loop and MCP are deliberately absent here. Phase 9 needs the owner's authorization.
