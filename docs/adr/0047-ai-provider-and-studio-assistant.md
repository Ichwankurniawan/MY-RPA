# ADR-0047: The AI Workflow Assistant: provider contract, pipeline and Studio integration

- Status: **Accepted** (owner, 2026-10-11).
  - Phase 8 authorized ("proceed for the next phase").
  - The owner's brief adopted ("replace your draft plan with mine, and proceed phase 8").
  - Choices: an OpenAI-compatible endpoint first, the key in the server's configuration or environment, workflow
    content sent only on request.
- Phase: 8. Brief: [ai-workflow-assistant-brief.md](../architecture/ai-workflow-assistant-brief.md). Plan:
  [ai-automation-plan.md](../architecture/ai-automation-plan.md).
- Amends:
  - ADR-0003 (two new `src` projects);
  - ADR-0014 / ADR-0039 (a second extension-contract assembly that plugins may reference);
  - ADR-0042 ("HTTP only in `plugins/MyRPA.Http`": an AI provider plugin may also call its configured endpoint).

## Context

The brief asks for an assistant that:
- turns natural language into workflows in the existing schema;
- asks for clarification instead of inventing requirements;
- uses only real catalog activities;
- validates every proposal deterministically and repairs within bounds;
- edits existing workflows under review, and explains validation errors and failed runs.

None of it may execute, bypass validation, generate code as a shortcut, or depend on one vendor.

The repository already forbids AI SDKs and `HttpClient` in `src`. It has:
- a catalog with the metadata the brief lists (catalog 1.2);
- the validator (`WorkflowLoader`);
- snapshot undo and ETags in the Studio;
- run events that carry no workflow data.

## Decision

1. **`MyRPA.Ai.Contracts`** is a new `src` project, BCL only, with no references.
   - **It holds:**
     - `IAiProvider` (`Info`, `CompleteAsync`);
     - `AiRequest` (instructions, messages, an optional JSON schema, an output limit);
     - `AiCompletion` (text, model, usage);
     - `AiProviderException` with a kind: NotConfigured, Authentication, RateLimited, Network, Timeout,
       InvalidResponse, UnsupportedStructuredOutput or TooLarge.
   - **Cancellation** is the caller's token (`OperationCanceledException`).
   - **Registration:** it is an extension-contract assembly (ADR-0039). Plugins register an `IAiProvider` and the host
     reads them as a list.
2. **`MyRPA.Ai.Assistant`** is a new `src` library. It references Core, Workflow and Ai.Contracts, and contains no
   HTTP. It holds the pipeline:
   - **Metadata** from the activity catalog via `ActivityCatalogJson`. There is no separate list. `Code.*` activities
     are left out and refused, because the brief's §4.5 forbids code as a shortcut.
   - **Redaction** of secret-looking literals in what is sent.
   - **One structured answer kind:** clarify, unsupported or proposal. Questions, known facts and assumptions are kept
     apart.
   - **Deterministic checks** that never trust the model:
     - the response size, and the JSON parse;
     - the schema version (the base document's version when editing; otherwise 1.0, or 1.1 for flowcharts);
     - `WorkflowLoader` (types, properties, slots, value types, expressions);
     - the catalog policy;
     - the workflow id kept when editing.
   - **Bounded repair** with the diagnostics, each attempt revalidated. The last invalid proposal is kept.
   - **Explain, fix-validation and debug requests.** Debug uses the run's error (code, redacted message, node, type,
     error type) and never run values.
   - **Lifecycle logging** without prompts or workflows.
3. **Providers are plugins.** The first is `plugins/MyRPA.Ai.OpenAiCompatible`, using the BCL `HttpClient` and no SDK
   package.
   - **Transport:** HTTPS, except to a loopback address; no redirects; TLS validation never off.
   - **The key:** from the environment variable named in its settings. It is sent only to that host and never logged.
   - **Reliability:** bounded retries only for rate limits and transient failures, and a timeout per attempt.
   - **Capabilities:** it states whether it supports JSON-schema output. Without that, the pipeline asks for JSON and
     validates it the same way.
4. **The server hosts the endpoints**, behind the session, Origin and anti-forgery checks:
   - `GET /api/assistant`: available, or why not;
   - `POST /api/assistant/preview`: exactly what would be sent;
   - `POST /api/assistant/generate`;
   - `POST /api/assistant/explain`;
   - `POST /api/assistant/debug`.

   Nothing the assistant returns starts a run.
5. **The Studio** gets an Assistant tab.
   - **Proposals are reviewed** before anything changes: the plan, what the workflow uses and touches, and a diff by
     node id.
   - **Applying:** a new workflow is created as a new file. An edit applies as one undo step, and is refused if the
     document changed since the request. Unsaved work is never discarded.
   - **Running** stays the user's separate action.

## Consequences

- Without a provider plugin, everything works as before, and the Assistant tab says why it is unavailable.
- Local models work through the same plugin, so workflow content can stay on the machine.
- Answers are not deterministic. Proposals still are only ever applied after deterministic validation and human
  review, and generation never runs anything.
- Phase 9 (agents, tools, MCP) can build on the contracts and the bounded operations, without an open-ended tool loop
  here.
