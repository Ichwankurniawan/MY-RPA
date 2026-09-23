# CLAUDE.md — MyRPA

Guidance for AI agents and contributors working in this repository.

## Phase discipline (most important)

- The project follows the phases in `MyRPA-PRD.md` §9. **Current phase: Phase 1 — Foundation (complete, awaiting review).**
- Never start the next phase without explicit user authorization ("Proceed to Phase N").
- Do not implement features from later phases "because the architecture anticipates them". Interfaces/placeholders only
  when the current phase genuinely needs them.
- End each phase with the PHASE COMPLETION REPORT (PRD §33). Never report a build/test PASS that was not actually run.

## Sources of truth

1. `MyRPA-PRD.md` — requirements and phases.
2. `docs/adr/` — accepted decisions (they refine the PRD; see `docs/architecture/phase-1-reconciliation.md`).
3. `docs/architecture/overview.md` — current architecture and enforced rules.
4. `docs/research/` — Phase 0 OpenRPA evidence (codes R#/D#/N# in `openrpa-analysis.md`).
5. `reference/openrpa/` — read-only OpenRPA clone (MPL-2.0). Never modify it; never copy its code into MyRPA.

## Commands

```bash
dotnet build MyRPA.sln
dotnet test --solution MyRPA.sln
dotnet run --project src/MyRPA.Cli -- info
```

On this workstation the SDK was installed user-locally to `%USERPROFILE%\.dotnet` (not on PATH). In Git Bash:
`export DOTNET_ROOT="$USERPROFILE/.dotnet" PATH="$USERPROFILE/.dotnet:$PATH"`.

## Architecture rules (enforced by `tests/MyRPA.Architecture.Tests`)

- Dependency direction (ADR-0003): Core ← Workflow; Core ← Activities; Core, Workflow ← Runtime; Core, Workflow ← Storage;
  everything ← Cli. Runtime must not reference Activities. Nothing references a composition root.
- `MyRPA.Core` and `MyRPA.Workflow`: BCL only, no packages, plain `net10.0` (ADR-0004).
- Libraries may use only `Microsoft.Extensions.*.Abstractions`; only composition roots use `Microsoft.Extensions.Hosting`.
- Forbidden in Phase 1 `src`: WPF/WinForms/XAML, Playwright/browser libs, FlaUI/UIA, WF4/CoreWF, database drivers/ORMs,
  AI SDKs, MCP SDKs, messaging/orchestrator stacks.
- No `async void`, no mutable static fields, no static service locators, no implicit discovery/assembly scanning (ADR-0005).
- No `Type.GetType(string)`, `Assembly.Load*`, `Activator.CreateInstance(string…)`, `BinaryFormatter`, .NET Remoting,
  default network listeners, or secrets in workflows/settings (ADR-0008).
- Adding a `src` project requires: an entry in `ArchitectureRules.SourceProjects`, a row in the overview's project table,
  and an ADR if it changes dependency direction.

## Design conventions

- **One workflow model** (`MyRPA.Workflow`) for CLI, Studio, Robot, Orchestrator and AI. Never create client-specific models.
- Activity types are referenced by registered name (`ActivityTypeName`, e.g. `Core.Log`), never by CLR type name.
- Activities carry no UI/designer concerns; metadata is data (`ActivityDescriptor`).
- Async-first: public async APIs take a `CancellationToken`; library code uses `ConfigureAwait(false)`.
- Inject `TimeProvider` instead of reading the clock; inject `ILogger<T>`; use `[LoggerMessage]` source-generated logging.
- Correlate logs and spans with `IExecutionScopeFactory` and the keys in `DiagnosticNames` (ADR-0006).
- Domain types are immutable; validate in constructors; identifiers are strongly typed.
- Public APIs in `src` have XML documentation (the build requires it).

## Code style

- `.editorconfig` is authoritative: file-scoped namespaces, `_camelCase` private fields, braces required.
- Warnings are errors. Suppress an analyzer only locally, with a justification comment.
- Package versions live in `Directory.Packages.props` (central package management). Adding a package needs a reason in
  the PR/commit message and must respect the allow-lists above.

## Tests

- xUnit v3 on Microsoft.Testing.Platform. Naming: `Subject_Condition_ExpectedResult`.
- Unit test projects reference only their subject project; `MyRPA.Integration.Tests` composes the real CLI host.
- Tests must be deterministic and must not use the network.
- When adding an architecture rule, also add a known-bad self-test for its detector.
