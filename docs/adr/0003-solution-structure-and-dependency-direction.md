# ADR-0003: Solution structure and dependency direction

- Status: Accepted; amended by [ADR-0010](0010-workflow-execution-model.md) (MyRPA.Activities may reference MyRPA.Workflow and Microsoft.Extensions.Logging.Abstractions)
- Date: 2026-09-23
- Phase: 1

## Context
PRD 1.2 proposes `Core, Workflow, Runtime, Activities, Storage, Cli` plus five test projects, and allows adjustment
after Phase 0. Phase 0 found that OpenRPA's contracts assembly (`OpenRPA.Interfaces`) carried WPF, FlaUI, NLog,
Remoting and registry code, so every plugin inherited the whole desktop stack (D5), and that nothing enforced
boundaries (no tests).

## Decision
Keep the PRD projects, with three refinements:

1. **Activity identity/metadata contracts live in `MyRPA.Core`** (`ActivityTypeName`, `ActivityDescriptor`,
   `IActivityCatalog`). `MyRPA.Activities` contains the catalog implementation and, from Phase 2, built-in activities.
   Future plugin authors reference Core only.
2. **`MyRPA.Runtime` does not reference `MyRPA.Activities`.** The engine must not depend on a specific activity
   library; the composition root decides which activities are registered.
3. **Add `tests/MyRPA.Architecture.Tests`** to enforce the rules below.

Allowed project references (anything else fails `MyRPA.Architecture.Tests`):

| Project | May reference |
|---|---|
| MyRPA.Core | — |
| MyRPA.Workflow | Core |
| MyRPA.Activities | Core |
| MyRPA.Runtime | Core, Workflow |
| MyRPA.Storage | Core, Workflow |
| MyRPA.Cli | Core, Workflow, Activities, Runtime, Storage |

## Alternatives
- Activity contracts in `MyRPA.Activities` (literal PRD reading) — plugins would depend on built-in implementations.
- A separate `MyRPA.Abstractions`/`MyRPA.Sdk` project — Core already plays this role; an extra project adds no boundary
  today. Revisit in Phase 3 if the plugin SDK needs a separately versioned package.
- Architecture tests inside `MyRPA.Integration.Tests` — mixes concerns; integration tests should not need to reference
  every project.

## Consequences
- The CLI (and later Studio/Robot hosts) is the only place that knows the full graph.
- Adding a project requires updating the allow-list in `MyRPA.Architecture.Tests` — intentional friction.
