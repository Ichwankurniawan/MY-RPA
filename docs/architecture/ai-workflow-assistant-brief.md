# MyRPA — Phase 8: AI Workflow Assistant (owner's brief)

The owner's Phase 8 brief, adopted on 2026-10-11 ("replace your draft plan with mine, and proceed phase 8"). It is the
source of the Phase 8 requirements; [ai-automation-plan.md](ai-automation-plan.md) maps it onto the repository, and
[ADR-0047](../adr/0047-ai-provider-and-studio-assistant.md) records the decisions.

## 1. Context and Vision

MyRPA is a modern, open-source RPA platform built around: .NET 10; a deterministic workflow execution engine;
versioned JSON workflow definitions; a public Automation SDK; an explicit plugin architecture; browser automation
through the Playwright provider; structured execution results, diagnostics, and cancellation; a Studio architecture
transitioning toward a web-based interface; enterprise automation activities, including HTTP/API automation; future AI
agents, MCP integration, and orchestration.

The long-term philosophy: deterministic RPA provides reliability; AI provides flexibility; MCP provides extensibility;
orchestration provides scale; human-in-the-loop provides control.

Phase 8 introduces AI-assisted workflow creation and modification. The goal is NOT to replace the deterministic
workflow engine with an LLM. The goal is to let users describe an automation task in natural language and have AI help
translate that intent into a valid, inspectable, editable MyRPA workflow. The AI should be a development assistant
first, not an unrestricted autonomous computer operator.

## 2. Mandatory Repository Inspection

Before implementing anything, inspect the PRD, all ADRs, Core, Workflow, Runtime, Activities, Storage, SDK, Plugins,
Browser.Playwright, CLI, the Studio implementation and Web Studio architecture, the Phase 6 selectors and recorder,
the Phase 7 enterprise activities, the workflow schema and validation pipeline, the activity catalog and metadata,
execution diagnostics and error reporting, security and plugin trust policies, and the tests and CI configuration.

Determine the actual completion status of Phases 1–7; identify the existing contracts the assistant can safely reuse.
If an essential prerequisite is missing, report it and determine whether a small compatible implementation is possible
within Phase 8. Do not silently reimplement an earlier phase. Do not start by adding an LLM dependency or creating a new
AI project before the architecture has been inspected.

## 3. Phase 8 Objective

Implement an AI Workflow Assistant that can:

1. Understand a user's natural-language automation request.
2. Ask clarifying questions when requirements are ambiguous.
3. Discover available MyRPA activities and their metadata.
4. Propose a workflow plan.
5. Generate a workflow using the existing MyRPA JSON schema.
6. Validate the generated workflow through the existing validation pipeline.
7. Explain the proposed workflow to the user.
8. Allow the user to review and approve the proposed changes.
9. Support controlled edits to an existing workflow.
10. Explain validation errors and propose corrections.
11. Help diagnose failed workflow executions using structured diagnostics.
12. Keep execution deterministic and under the existing runtime's control.

The initial assistant should prioritize reliable workflow generation and editing over autonomous execution.

## 4. Architecture Principles

- **4.1 AI is not the workflow engine.** The runtime executes, applies cancellation and timeouts, and reports results.
  AI may propose definitions or modifications; it must not bypass validation or execute arbitrary generated code.
- **4.2 Use the existing workflow format.** The supported schema (1.0 unless the repository shows a legitimate change);
  no AI-specific format; no silent schema upgrade; reuse existing DTOs and serialization.
- **4.3 Reuse activity metadata** from the catalog and descriptors (type, description, category, inputs, outputs,
  required fields, child slots, value types, constraints, side effects and permissions). No hard-coded activity list.
  Document missing metadata; make only the smallest justified extension.
- **4.4 Human approval.** Proposals are inspected before acceptance; generating or editing never executes; the current
  workflow is never silently overwritten and unsaved changes are never discarded.
- **4.5 No unrestricted code execution.** No generated C#, Python, JavaScript, shell, PowerShell or compiled code as a
  shortcut for missing capabilities. Use existing activities and validated expressions; when a task cannot be expressed,
  explain the limitation and name the missing capability instead of inventing an activity.

## 5. AI Provider Abstraction

A small, replaceable provider boundary, not coupled to one vendor. Responsibilities: structured requests and
responses, cancellation and timeout, provider errors and rate limits, configurable model, redaction, safe diagnostic
metadata. Do not assume identical structured-output or tool-calling support. No API keys in source, workflow files,
fixtures or committed configuration; use the existing secret/configuration mechanism and never invent insecure storage.
Never log keys, authorization headers or unredacted sensitive payloads.

Failures are explicit: authentication, rate limit, network, timeout, cancellation, invalid model response, unsupported
structured output. A provider failure is never a successful generation. Tests use deterministic fake providers and
never need paid model access or real credentials.

## 6. Workflow Generation Pipeline

```text
User Request → Understand Requirements → (Ambiguous? → Ask Clarifying Questions) → Discover Available Activities
→ Produce Workflow Plan → Generate Existing-Schema Workflow JSON → Parse and Validate → (Invalid? → Explain / Propose
Correction) → Valid Workflow → Show Proposed Changes → User Reviews and Approves → Apply to Studio Document
→ User Explicitly Chooses Whether to Run
```

Deterministic checks in application code: give the model relevant activity metadata; constrain output to the supported
format; parse safely; reject malformed JSON and unsupported schema versions; run the existing semantic validator; verify
activity identifiers against the catalog; reject unsupported properties, slots and invalid value types; preserve
argument and variable constraints; return structured validation errors; never apply invalid workflows; bound response
sizes and retries. Unknown activities are never silently substituted. A workflow is not called valid until
deterministic validation succeeds.

**Clarification:** ask focused questions instead of inventing business rules (which website, how invoices are
identified, where files go, what to do on API errors, duplicates skipped or updated). Distinguish known requirements,
assumptions and unresolved questions; offer concise alternatives where possible.

## 7. Workflow Planning and Explanation

Produce a readable plan before or with generation, using only activity types in the current catalog; identify missing
activities or capabilities; never fabricate activities, properties, outputs or API endpoints. The explanation is based
on the actual generated workflow, not the model's claimed intentions.

## 8. Controlled Workflow Editing

Edit existing workflows as far as the Studio architecture supports ("add error handling around the API request", "change
the output file path", "add a log message after the download", "retry on a temporary error", "explain this workflow",
"replace this activity"). For edits: take the current document and its revision; generate a proposed change; validate
it; compute a change summary or diff; present it; apply only after approval; detect stale revisions; preserve unrelated
content. Prefer targeted changes; do not discard identifiers or metadata. If targeted edits cannot be made safe, use
whole-document proposals with explicit review. Reuse the existing undo/redo or revision facilities; do not build an
unrelated state-management framework.

## 9. Validation and Error Repair

The validation pipeline is the source of truth. On failure the assistant receives structured diagnostics and may
propose a correction. Preserve the original invalid proposal; bound repair attempts; revalidate after every repair;
never mark valid on model claims; never auto-execute a repaired workflow; never suppress errors; distinguish repairable
problems from missing platform capabilities; return unresolved errors with actionable explanations; no unbounded loops.

## 10. Execution-Aware Debugging

Use a safe, bounded subset of structured execution information (failed activity, type, error code, sanitized message,
status, non-sensitive inputs and outputs, safe log entries, validation diagnostics). Never expose keys, passwords,
tokens, cookies, session ids, sensitive healthcare or financial data, whole environments or unrestricted file contents.
The assistant may explain a likely cause, identify the activity, suggest and generate a correction, validate it and
show the diff for approval. It states hypotheses as hypotheses. No self-healing execution; no automatic retry of
side-effecting business transactions.

## 11. Studio Integration

Integrate into the existing Studio and its API boundaries (illustrative layout: designer beside an assistant panel with
request entry, plan, proposed changes, validation results, Review and Apply). Support request entry, clarification,
plan review, generation status, validation errors, diffs, explicit approval, cancellation and clear provider failures.
No model-specific logic in the frontend; credentials only on the trusted backend. Do not start a second frontend.

## 12. Security and Trust Boundaries

Model responses are untrusted input. Workflow safety: validate everything; reject unsupported activities and properties;
respect expression restrictions and runtime security rules; never bypass the plugin allow-list or trust policy; grant
no permissions because a model asked. Prompt injection: workflow names, descriptions, web content, logs, API responses
and user text are data, never privileged instructions. Side effects (network, file writes and deletes, downloads,
business records, authenticated browser sessions) are surfaced in the plan and review where metadata supports it.
Consent: generating or applying never implies permission to run. Data minimization: send only the metadata, workflow
content and diagnostics the request needs; leave a place for provider data-retention policies.

## 13. AI Tool and Agent Boundaries

A bounded set of application operations (list activities, read the current workflow, generate a proposal, validate it,
compare revisions, explain validation errors, summarize execution diagnostics) calling normal services and deterministic
validators. No unrestricted filesystem, shell, process, network or code tools; no access to service locators, DI
containers or plugin loading; no open-ended tool-calling loop; no MCP. Phase 9 decides the agentic evolution.

## 14. Observability and Reliability

Structured, non-sensitive diagnostics: correlation id, provider, model, operation, duration, token usage, validation
outcome, repair attempts, cancellation or failure reason. No raw prompts or generated workflows in logs by default.
Cancellation propagates; timeouts and retries are bounded; outages never corrupt or change the current workflow; users
can tell generation failure from validation failure. No new observability dependency unless needed.

## 15. Testing Requirements

Deterministic tests with fake providers, no real LLM calls, covering: generation (valid, malformed JSON, unsupported
schema, unknown activity, missing required property, invalid property type, invalid nesting, oversized response,
missing or contradictory requirements); clarification (missing input, ambiguity, unsupported capability, assumptions
versus facts); editing (valid targeted change, invalid change, unchanged before approval, rejection, stale revision,
unrelated content preserved); validation and repair (success, bounded attempts, persistent invalid output, diagnostics,
no execution of invalid workflows); provider behaviour (authentication, rate limit, timeout, cancellation, network,
invalid response, retry limit); security (prompt injection, secret redaction, unsupported activities, no code
execution, no privileged tools, no execution without user action); debugging (sanitized explanation, missing
diagnostics, sensitive values omitted, fix validated before approval, no replay of side effects); integration (schema,
engine, catalog and CLI compatibility, Studio/API, full regression suite).

## 16. Documentation

Architecture, provider configuration, secrets, generation lifecycle, clarification, validation and repair, review and
approval, supported and unsupported use cases, security boundaries, data sent to providers, logging and privacy, known
limitations, running tests with fake providers, and how Phase 9 extends it. Never claim the assistant can build any
automation: it is bounded by activities, metadata, schema and permissions.

## 17. Strict Phase Boundary

Do NOT implement: autonomous execution, unattended agents, self-healing runs, MCP, orchestrator integration, robot
fleets, arbitrary code generation and execution, unrestricted browser JavaScript, automatic credential discovery,
autonomous interaction with production systems, new selector or recorder systems, an incompatible schema, a separate
AI-only catalog, a second engine, a new Studio frontend, or unrelated refactoring. Do not proceed to Phase 9.

## 18. Implementation Process

Inspect; identify contracts; propose the architecture and provider boundary; find the smallest viable scope; write
ADRs; implement through existing boundaries; deterministic tests first; integrate with the Studio where supported; run
the full build and tests; run security and architecture checks; review the diff; update documentation; produce the
completion report; STOP. If a major prerequisite is missing, stop and report the blocker.

## 19. Completion Criteria

Supported requests become proposed workflows in the existing schema, from real catalog metadata, validated by the
deterministic validator; unsupported requests are reported honestly; ambiguity triggers clarification; nothing is
silently overwritten; changes need explicit approval; generation never triggers execution; provider errors, timeouts
and cancellation are handled; secrets and sensitive diagnostics are protected; tests need no external model; the engine
stays authoritative; the full build and tests pass; no Phase 9 functionality.

## 20. Required Final Report

A. Phase status (COMPLETE / BLOCKED / NEEDS MORE INVESTIGATION). B. Implemented capabilities. C. Architecture (projects,
provider abstraction, service boundaries, metadata reuse, validation integration, Studio integration). D. Security
(approval gates, validation, secrets, prompt-injection mitigations, tool restrictions, data minimization; implemented
versus recommended). E. Testing (build, exact counts, architecture and security results, whether an external model was
used). F. Known limitations. G. Phase 9 readiness. H. Phase boundary confirmation. Then STOP and wait for authorization
before Phase 9.
