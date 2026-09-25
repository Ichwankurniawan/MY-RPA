# Architecture Decision Records

Format and rules: [ADR-0001](0001-record-architecture-decisions.md).

| ADR | Title | Status | Phase |
|---|---|---|---|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions in `docs/adr/` | Accepted | 1 |
| [0002](0002-own-engine-and-versioned-json-workflow-model.md) | Own workflow engine over a versioned JSON workflow model | Accepted | 1 |
| [0003](0003-solution-structure-and-dependency-direction.md) | Solution structure and dependency direction | Accepted, amended by 0010, 0013 and 0018 | 1 |
| [0004](0004-platform-neutral-core.md) | Platform-neutral Core with zero package dependencies | Accepted, WPF allowed in MyRPA.Studio only by 0018 | 1 |
| [0005](0005-explicit-composition-generic-host.md) | Explicit composition with Generic Host and DI; no global state | Accepted | 1 |
| [0006](0006-observability-foundation.md) | Observability foundation — ILogger scopes and ActivitySource | Accepted | 1 |
| [0007](0007-testing-strategy-and-architecture-tests.md) | Test stack and architecture-rule tests | Accepted | 1 |
| [0008](0008-secure-by-default-baseline.md) | Secure-by-default baseline | Accepted, extended by 0012, 0014 and 0015 | 1 |
| [0009](0009-constrained-expression-language.md) | Constrained, in-house expression language | Accepted | 2 |
| [0010](0010-workflow-execution-model.md) | Workflow execution model, contracts and lifetimes | Accepted, activity lifetime amended by 0013 | 2 |
| [0011](0011-workflow-json-format-and-validation-pipeline.md) | Workflow JSON format v1.0 and the validation pipeline | Accepted | 2 |
| [0012](0012-invoke-workflow-resolution-and-limits.md) | InvokeWorkflow resolution, confinement and limits | Accepted | 2 |
| [0013](0013-automation-sdk-and-activity-contract.md) | Automation SDK and the frozen activity contract | Accepted | 3 |
| [0014](0014-plugin-manifest-lifecycle-and-loading.md) | Plugin manifest, lifecycle and AssemblyLoadContext loading | Accepted, loading amended by 0016 | 3 |
| [0015](0015-plugin-trust-model.md) | Plugin trust model | Accepted, integrity guarantees amended by 0016 | 3 |
| [0016](0016-verified-path-based-plugin-loading.md) | Verified path-based plugin loading | Accepted | 4 |
| [0017](0017-browser-automation-provider.md) | Browser automation provider (Playwright plugin) | Accepted | 4 |
| [0018](0018-studio-architecture.md) | Studio architecture (platform-neutral Studio.Core, WPF shell) | Accepted | 5 |
| [0019](0019-plugin-configuration-file.md) | Plugin configuration file | Accepted | 5 |
| [0020](0020-activity-catalog-snapshot.md) | Activity catalog snapshot | Accepted | 5 |
