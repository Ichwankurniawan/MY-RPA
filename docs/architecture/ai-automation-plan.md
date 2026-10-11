# Phase 8: AI Automation (plan)

Authorized by the owner on 2026-10-11 ("proceed for the next phase"). Decisions:
[ADR-0047](../adr/0047-ai-provider-and-studio-assistant.md). Requirements: `MyRPA-PRD.md` Phase 8 (8.1–8.4).

## Owner choices (2026-10-11)

| Question | Choice |
|---|---|
| First provider | An **OpenAI-compatible endpoint**: OpenAI, Azure OpenAI and local servers (Ollama, LM Studio). More providers come later behind `IAiProvider`. |
| API key | In the **server's configuration or environment**, never in the browser or a workflow file |
| Workflow content sent to the provider | Yes, **only when the user asks**; the Studio shows what is sent |
| First features | **Explain** and **Debug** (read-only), then Generate workflow and Generate activity |

## The rule that shapes everything (PRD 8.4)

AI never executes anything and never changes a workflow by itself:
- The assistant's answers are text and node references.
- Generated workflows and activities (A-3, A-4) are drafts. A draft goes through the server's validation and a policy
  check, and is applied only when a person accepts it.
- Running stays where it is: `ExecutionHost`, on a saved or validated document.

## Architecture

```text
Studio (Assistant tab) ──POST /api/assistant/*──▶ MyRPA.Server (AssistantService: prompt, redaction, output checks)
                                                     │ IAiProvider (MyRPA.Ai.Contracts, BCL only)
                                                     ▼
                                   plugins/MyRPA.Ai.OpenAiCompatible ──HTTPS──▶ the configured endpoint
```

- **`src/MyRPA.Ai.Contracts`** (new, no references, BCL only, like `MyRPA.Browser.Contracts`):
  - `IAiProvider` (`Info`, `CompleteAsync`);
  - `AiRequest`: system text, messages, an optional JSON schema for the answer, and an output limit;
  - `AiCompletion`: text, model and token usage;
  - `AiProviderException`, with a kind: NotConfigured, Authentication, RateLimited, Unavailable, TooLarge or
    InvalidResponse.
- **`plugins/MyRPA.Ai.OpenAiCompatible`**: implements `IAiProvider` with the BCL `HttpClient` against
  `{endpoint}/chat/completions`. It uses no SDK package.
  - **Settings:** `endpoint`, `model`, `apiKeyVariable` (the name of the environment variable holding the key),
    `timeoutMs` and `maxRequestChars`.
  - **Transport:** HTTPS, except a loopback endpoint (for local models); no redirects; TLS validation never off.
  - **The key** is sent only to the endpoint's host and is never logged.
- **`MyRPA.Server`** references the contracts.
  - `AssistantService` builds prompts from the document and the activity catalog. It checks the answer against its
    schema and drops node ids that are not in the document.
  - **Endpoints:**
    - `GET /api/assistant`: available, provider and model, or why not;
    - `POST /api/assistant/preview`: exactly what would be sent;
    - `POST /api/assistant/explain`;
    - `POST /api/assistant/debug`.
- **What is sent:**
  - the workflow being edited, including its literal default values;
  - the catalog entries of the activity types it uses;
  - for Debug, the run's error (code, message, node, activity type, error type) and node outcomes.
- **Never sent:** run values (events carry none, ADR-0023), argument text typed in the Run dialog, plugin settings,
  secrets, or files.

## Slices

| Slice | Delivers | Tests |
|---|---|---|
| **A-1** | `MyRPA.Ai.Contracts` and the OpenAI-compatible plugin; `GET /api/assistant`, `preview` and `explain`; the architecture rules (the new `src` project, plugin references, package-free HTTP) | Plugin tests against a local fake endpoint (request shape, structured output, errors mapped, key never logged, no redirects, http refused unless loopback); server tests with a fake provider (explain, preview, unknown node ids dropped, unavailable, session and anti-forgery); architecture self-tests |
| **A-2** | Studio **Assistant** tab: availability, **Explain this workflow** with links to nodes, **What is sent**; **Ask why it failed** on a failed run (`debug`) | Vitest (FakeApi), a11y, a smoke step with a fake provider |
| **A-3** | **Generate workflow** (PRD 8.3): natural language → structured draft → `WorkflowLoader` validation → policy (catalog activities only; side effects and `Code.*` called out) → human review → saved as a new file | Server and Vitest tests: invalid drafts refused with diagnostics, the policy, nothing saved without acceptance |
| **A-4** | **Generate activity**: a fragment for the selection, through the same validation and review, inserted with undo | As A-3 |
| **A-5** | Docs, end-to-end runs and the phase completion report | Full build, tests, smoke, a11y, perf |

Each slice ends green (build, `dotnet format`, all tests, Vitest, and the browser checks it touches) and is merged
only on the owner's word.

## Out of scope

- Phase 9 (AI agents, MCP, agent activities).
- AI that runs, schedules or publishes anything.
- Storing conversations.
- Providers other than OpenAI-compatible endpoints (later, behind the same interface).
