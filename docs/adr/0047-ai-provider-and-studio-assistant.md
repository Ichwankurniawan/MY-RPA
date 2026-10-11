# ADR-0047: The AI provider contract and the Studio assistant

- Status: **Accepted** (owner, 2026-10-11: "proceed for the next phase"; choices: an OpenAI-compatible endpoint first,
  the key in the server's configuration or environment, workflow content sent only on request, Explain and Debug
  first).
- Phase: 8 (AI Automation). Plan: [ai-automation-plan.md](../architecture/ai-automation-plan.md).
- Amends:
  - ADR-0003 (a new contracts project);
  - ADR-0014 / ADR-0039 (the references a plugin may have);
  - ADR-0042 ("HTTP only in `plugins/MyRPA.Http`": an AI provider plugin may also call its configured endpoint).

## Context

PRD Phase 8 asks for an `IAiProvider` for several providers, a Studio assistant (explain, generate an activity,
generate a workflow, debug), and an AI workflow builder whose output is validated and reviewed. Its critical rule: AI
must not execute arbitrary actions, and AI-generated workflows must pass validation before execution.

The architecture rules already forbid AI SDKs and `HttpClient` in `src`, and keep technology in plugins.

## Decision

1. **`MyRPA.Ai.Contracts`** is a new `src` project, BCL only and with no references, like `MyRPA.Browser.Contracts`.
   - **It holds:** `IAiProvider`, `AiRequest`, `AiCompletion` and `AiProviderException` (with a kind).
   - **Who references it:** `MyRPA.Server`, and plugins that implement it. Plugins register their implementation as an
     extension contract, as in ADR-0039.
   - **Who never references it:** the engine, the Sdk and the plugin host.
2. **Providers are plugins.** The first is `plugins/MyRPA.Ai.OpenAiCompatible`, for the chat-completions format of
   OpenAI, Azure OpenAI and local servers.
   - **No SDK package:** it uses the BCL `HttpClient`.
   - **Its endpoint only:** HTTPS (plain HTTP only to a loopback address), no redirects, and TLS validation never off.
   - **The key:** read from the environment variable named in its settings (`apiKeyVariable`), sent only to that
     host, and never logged or returned.
3. **The server owns the prompts and checks the answers.**
   - `AssistantService` builds each request from the edited document and the catalog entries of the activity types it
     uses. For Debug it adds the run's error and node outcomes.
   - **Never sent:** run values, typed argument text, plugin settings, secrets or files.
   - **`POST /api/assistant/preview`** returns exactly what would be sent, so the Studio can show it.
   - **Answers** are structured JSON checked against a schema. Node ids that are not in the document are dropped.
4. **Read-only first.** Explain and Debug only return text and node references; the Studio shows them.
   - Generate workflow and Generate activity come later. They produce drafts that go through `WorkflowLoader` and a
     policy check, and are applied only when a person accepts them (PRD 8.3).
   - Nothing the assistant returns ever starts a run.
5. **On request only.** A request is made only when the user presses an assistant action. When no provider is
   configured, `GET /api/assistant` says why, and the Studio shows that instead of the actions.

## Consequences

- A workstation without a provider plugin works as before.
- Local models (Ollama, LM Studio) work through the same plugin, so workflow content can stay on the machine.
- Adding another provider (for example Anthropic's Messages API) is a new plugin behind the same interface, without
  changing the server or the Studio.
- Answers depend on the model and are not deterministic. That is acceptable because they are advice, never actions.
