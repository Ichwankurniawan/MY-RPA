# Phase 10 plan: Orchestrator, Server and distributed Runners

- **Authorized** by the owner on 2026-10-12: the Phase 10 brief, then "Yes, proceed to Phase 10".
- **Choices:** SQLite (ADR-0050); roles with access tokens; a separate HTTPS Runner port (ADR-0051).
- **Decision record:** ADR-0049. **Guide:** [orchestrator.md](orchestrator.md).

## What was reused

| Need | Reused |
|---|---|
| Executing a workflow, events, cancellation, time limits | `ExecutionHost` (ADR-0022/0023), unchanged |
| Validation and argument conversion | `WorkflowLoader`, `WorkflowValues` |
| Plugins on Runners | `PluginConfigurationFile`, `PluginLoader` (ADR-0014/0019) |
| Event model | `ExecutionEventMessage` (sequence numbers give idempotent storage) |
| Studio | `ProjectStore` (publish), the store, rail, tokens and API conventions |
| AI agents | `AI.Agent` (ADR-0048), unchanged; its events are log events |

## Slices (all done on 2026-10-12)

| Slice | Content | Evidence |
|---|---|---|
| O-1 | `MyRPA.Contracts.Orchestration` (Runner protocol v1); `MyRPA.Orchestration` (state machine, packages, dispatch, leases, schedules, cron, tokens); `MyRPA.Orchestration.Sqlite` | `MyRPA.Orchestration.Tests` (117) |
| O-2 | Server: `--data`, `--runner-url`, `--runner-cert`; Runner listener; bearer tokens; `/api/orchestrator/*`; `/runner/v1/*`; sweep and schedule background service | `MyRPA.Runner.Tests` (security, protocol) |
| O-3 | `src/MyRPA.Runner` (`myrpa-runner run`/`check`, Windows service), `RunnerConnection` (the only `HttpClient`) | `MyRPA.Runner.Tests` (13, real Server and Runner) |
| O-4 | Architecture rules: three src projects, SQLite owner, `HttpClient` exemption with self-checks | `MyRPA.Architecture.Tests` |
| O-5 | Studio: Deployments, Monitoring, Robots, Admin pages (`orchestrator.ts`, `Operations.tsx`) | `OperationsUi.test.tsx` (9); `npm run operations` (real browser, Server and Runner process) |
| O-6 | ADR-0049/0050/0051, this plan, the guide, overview, server and web-studio docs, CLAUDE.md | — |

## Deferred (stated, not built)

- Work-item queues (PRD 10.4).
- Event triggers.
- Human approval and resume.
- Checkpoint and resume.
- Runner auto-update and fleet deployment.
- Clustering of Servers.
- User accounts, login and SSO (Phase 11).
- Desktop automation in a service session.
