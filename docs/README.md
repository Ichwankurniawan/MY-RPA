# MyRPA documentation

| Document | Contents |
|---|---|
| [architecture/overview.md](architecture/overview.md) | Projects, dependency direction, enforced rules, composition |
| [architecture/execution-model.md](architecture/execution-model.md) | Engine: loading, execution, activity execution, statuses, lifetimes, observability |
| [architecture/workflow-format.md](architecture/workflow-format.md) | Workflow JSON v1.0, built-in activities, expressions, diagnostic and error codes |
| [architecture/automation-sdk.md](architecture/automation-sdk.md) | Automation SDK 1.0: activity contract, lifetime, results/failures, cancellation, providers, elements, selectors |
| [architecture/plugin-system.md](architecture/plugin-system.md) | Plugins: manifest, lifecycle, AssemblyLoadContext loading, trust model, diagnostics, CLI usage |
| [architecture/browser-automation.md](architecture/browser-automation.md) | Playwright browser plugin: sessions, activities, selectors, errors, security, browser installation |
| [architecture/server.md](architecture/server.md) | MyRPA.Server (local mode): start, security, HTTP API, multiplexed event stream |
| [architecture/web-studio.md](architecture/web-studio.md) | Web Studio (W3 slice and W4A structural editing): run, what it does, structure, tests, deferred work |
| [architecture/studio.md](architecture/studio.md) | MyRPA Studio: projects, document model, designer, validation, running, manual test script |
| [architecture/phase-1-reconciliation.md](architecture/phase-1-reconciliation.md) | How the PRD was reconciled with the Phase 0 findings |
| [adr/](adr/README.md) | Architecture Decision Records (the PRD's `docs/decisions/` lives here; see ADR-0001) |
| [research/w0-web-studio-spike.md](research/w0-web-studio-spike.md) | W0 Web Studio spike: React designer and SSE measurements behind ADR-0021 to ADR-0025 |
| [research/](research/openrpa-overview.md) | Phase 0 OpenRPA reverse-engineering research and analysis |

Product requirements: [../MyRPA-PRD.md](../MyRPA-PRD.md). Contributor/agent rules: [../CLAUDE.md](../CLAUDE.md).
Example workflows: [../samples/](../samples). Sample plugin: [../samples/plugins/](../samples/plugins/README.md).
