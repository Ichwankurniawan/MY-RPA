# Architecture Decision Records

Format and rules: [ADR-0001](0001-record-architecture-decisions.md).

| ADR | Title | Status | Phase |
|---|---|---|---|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions in `docs/adr/` | Accepted | 1 |
| [0002](0002-own-engine-and-versioned-json-workflow-model.md) | Own workflow engine over a versioned JSON workflow model | Accepted | 1 |
| [0003](0003-solution-structure-and-dependency-direction.md) | Solution structure and dependency direction | Accepted, amended by 0010, 0013, 0018 and 0022 | 1 |
| [0004](0004-platform-neutral-core.md) | Platform-neutral Core with zero package dependencies | Accepted, WPF allowed in MyRPA.Studio only by 0018 (temporary, see 0021); ASP.NET Core in server executables by 0022 | 1 |
| [0005](0005-explicit-composition-generic-host.md) | Explicit composition with Generic Host and DI; no global state | Accepted | 1 |
| [0006](0006-observability-foundation.md) | Observability foundation — ILogger scopes and ActivitySource | Accepted | 1 |
| [0007](0007-testing-strategy-and-architecture-tests.md) | Test stack and architecture-rule tests | Accepted | 1 |
| [0008](0008-secure-by-default-baseline.md) | Secure-by-default baseline | Accepted, extended by 0012, 0014 and 0015 | 1 |
| [0009](0009-constrained-expression-language.md) | Constrained, in-house expression language | Accepted | 2 |
| [0010](0010-workflow-execution-model.md) | Workflow execution model, contracts and lifetimes | Accepted, activity lifetime amended by 0013 | 2 |
| [0011](0011-workflow-json-format-and-validation-pipeline.md) | Workflow JSON format v1.0 and the validation pipeline | Accepted, amended by 0026 | 2 |
| [0012](0012-invoke-workflow-resolution-and-limits.md) | InvokeWorkflow resolution, confinement and limits | Accepted | 2 |
| [0013](0013-automation-sdk-and-activity-contract.md) | Automation SDK and the frozen activity contract | Accepted | 3 |
| [0014](0014-plugin-manifest-lifecycle-and-loading.md) | Plugin manifest, lifecycle and AssemblyLoadContext loading | Accepted, loading amended by 0016 | 3 |
| [0015](0015-plugin-trust-model.md) | Plugin trust model | Accepted, integrity guarantees amended by 0016 | 3 |
| [0016](0016-verified-path-based-plugin-loading.md) | Verified path-based plugin loading | Accepted | 4 |
| [0017](0017-browser-automation-provider.md) | Browser automation provider (Playwright plugin) | Accepted | 4 |
| [0018](0018-studio-architecture.md) | Studio architecture (platform-neutral Studio.Core, WPF shell) | Superseded by 0021 (WPF is a temporary reference, to be removed) | 5 |
| [0019](0019-plugin-configuration-file.md) | Plugin configuration file | Accepted | 5 |
| [0020](0020-activity-catalog-snapshot.md) | Activity catalog snapshot | Accepted | 5 |
| [0021](0021-web-first-studio-and-wpf-removal.md) | Web-first Studio; WPF temporary and removed at exit (with exit criteria) | Accepted | W0 |
| [0022](0022-server-control-plane-and-project-structure.md) | MyRPA.Server control plane and project structure | Accepted; Studio serving added in W3 | W0 |
| [0023](0023-first-class-execution-events.md) | First-class execution events (engine observer hook) | Accepted, implemented in W1 | W0 |
| [0024](0024-execution-event-streaming-sse.md) | Execution event streaming to browsers (SSE, one stream per tab) | Accepted, implemented in W2 | W0 |
| [0025](0025-local-mode-security.md) | Local-mode security for MyRPA.Server | Accepted, implemented in W2 | W0 |
| [0026](0026-missing-property-diagnostic-location.md) | Missing-property diagnostics point at the property | Accepted, implemented in W2 | W2 |
| [0027](0027-invoke-workflow-confinement-in-projects.md) | InvokeWorkflow confinement in server projects | Keep ADR-0012 in W2; project-root resolver **Proposed, deferred** | W2 |
| [0028](0028-web-studio-first-slice.md) | Web Studio first slice (W3) | Accepted for W3; hand-written wire types **need approval** | W3 |
| [0029](0029-web-studio-structural-editing.md) | Web Studio structural editing and undo/redo (W4A) | Accepted for W4A | W4A |
