# CLAUDE.md — MyRPA

Guidance for AI agents and contributors working in this repository.

## Phase discipline (most important)

- The project follows the phases in `MyRPA-PRD.md` §9. **Current phase: Phase 5 complete (W0–W10, 2026-10-08;
  the WPF Studio is archived, ADR-0036). Authorized follow-up (owner, 2026-10-08): the Studio UX slice UX-1 → UX-3
  (`docs/architecture/studio-ux-plan.md`), then graph workflows (ADR-0037: flowchart, then state machine; `docs/architecture/graph-workflows-plan.md`,
  G-1 engine, G-2 Studio canvas and G-3 state machine done).** Phase 6
  (Selectors & Recorder) comes after those and must not start without the owner's explicit authorization.
- Never start the next phase without explicit user authorization ("Proceed to Phase N").
- Do not implement features from later phases "because the architecture anticipates them". Interfaces/placeholders only
  when the current phase genuinely needs them.
- End each phase with the PHASE COMPLETION REPORT (PRD §33). Never report a build/test PASS that was not actually run.

## Sources of truth

1. `MyRPA-PRD.md` — requirements and phases.
2. `docs/adr/` — accepted decisions (they refine the PRD).
3. `docs/architecture/overview.md`, `execution-model.md`, `workflow-format.md`, `automation-sdk.md`,
   `plugin-system.md`, `browser-automation.md`, `server.md`, `web-studio.md` — current architecture;
   `web-studio-roadmap.md` and `web-studio-parity.md` — how Phase 5 was completed (W-roadmap, WPF exit evidence).
4. `docs/research/` — Phase 0 OpenRPA evidence (codes R#/D#/N# in `openrpa-analysis.md`).
5. `reference/openrpa/` — read-only OpenRPA clone (MPL-2.0). Never modify it; never copy its code into MyRPA.

## Commands

```bash
dotnet build MyRPA.sln
dotnet test --solution MyRPA.sln
dotnet run --project src/MyRPA.Cli -- validate samples/control-flow.json
dotnet run --project src/MyRPA.Cli -- run samples/hello-world.json --arg userName=Ada
dotnet run --project src/MyRPA.Cli -- --plugin samples/plugins/MyRPA.Samples.DemoPlugin/bin/Debug/net10.0 run samples/plugins/demo-plugin.json
cd web/studio && npm ci && npm test && npm run build   # the Studio (npm.cmd in Windows PowerShell)
dotnet run --project src/MyRPA.Server -- --open samples/control-flow.json   # Studio + control plane (bundled Studio needs a build after npm run build)
dotnet run --project src/MyRPA.Server -- --project samples --web web/studio/dist --port 0   # serve web/studio/dist directly
cd web/studio && npm run smoke && npm run manual && npm run a11y && npm run perf   # end to end (needs the Release build)
dotnet format MyRPA.sln --verify-no-changes   # CI enforces naming rules the build does not
pwsh plugins/MyRPA.Browser.Playwright/bin/Debug/net10.0/playwright.ps1 install chromium   # once, for browser tests
```

On this workstation the SDK was installed user-locally to `%USERPROFILE%\.dotnet` (not on PATH). In Git Bash:
`export DOTNET_ROOT="$USERPROFILE/.dotnet" PATH="$USERPROFILE/.dotnet:$PATH"`.

## Architecture rules (enforced by `tests/MyRPA.Architecture.Tests`)

- Dependency direction (ADR-0003, amended by ADR-0010 and ADR-0013): Core ← Workflow; Core, Workflow ← Activities;
  Core, Workflow ← Runtime; Core, Workflow ← Storage; Core, Workflow ← Sdk; Core, Workflow, Sdk, Activities ← Plugins;
  everything ← Cli; Contracts has no references;
  Core, Workflow, Contracts ← Execution.Hosting (ADR-0022); engine, Plugins, Contracts, Execution.Hosting ← Server. The engine and plugin host never reference Contracts or
  Execution.Hosting. Runtime must not reference Activities. The engine and built-in libraries never reference Sdk or
  Plugins. Nothing references a composition root.
- `MyRPA.Core`, `MyRPA.Workflow` and `MyRPA.Sdk`: BCL only, no packages, plain `net10.0` (ADR-0004, ADR-0013).
- Plugin projects (`plugins/*`, `samples/plugins/*`, `tests/fixtures/*`) reference only `MyRPA.Sdk` (host contract
  assemblies with `Private="false"`), set `EnableDynamicLoading`, and are never compiled against: tests reference them
  with `ReferenceOutputAssembly="false"` and load them through the plugin host.
- Technology packages live only in their provider plugin (`ArchitectureRules.TechnologyPackageOwners`):
  `Microsoft.Playwright` only in `plugins/MyRPA.Browser.Playwright`. Never in src, tests or other plugins.
- Browser plugin rules (ADR-0017): no JavaScript evaluation (`EvaluateAsync`), http/https/about:blank URLs only, file
  access only through `BrowserFilePolicy`, one browser per session, sessions closed when the run ends.
- Libraries may use only `Microsoft.Extensions.*.Abstractions`; only composition roots use `Microsoft.Extensions.Hosting`.
- Forbidden everywhere: WPF/WinForms/XAML and Windows-only target frameworks (ADR-0036). Forbidden in `src`: Playwright/browser libs, FlaUI/UIA, WF4/CoreWF, DB drivers/ORMs, AI SDKs,
  MCP SDKs, messaging, ASP.NET Core (except the `MyRPA.Server` composition root, ADR-0022).
- No `async void`, no mutable static fields, no static service locators, no implicit discovery/assembly scanning (ADR-0005).
- Banned APIs (IL scan, ADR-0008/0012/0014): `Type.GetType(string)`, `Assembly.Load*`, `Assembly.GetType(string)`,
  `AssemblyLoadContext.LoadFrom*`, `Activator.CreateInstance(string…)`, `BinaryFormatter`, `Process.Start`,
  `HttpClient`/`WebClient`/`WebRequest`, sockets. The only exemption is `MyRPA.Plugins.Loading.PluginLoadContext`
  (`ArchitectureRules.BannedApiExemptions`); do not add others without an ADR.
- Never describe `AssemblyLoadContext` as a sandbox or security boundary: in-process plugins are fully trusted
  (ADR-0015).
- Adding a `src` project requires: an entry in `ArchitectureRules.SourceProjects`, a row in the overview's project table,
  and an ADR if it changes dependency direction.

## Engine conventions (Phase 2)

- **One workflow model** (`MyRPA.Workflow`) for CLI, Studio, Robot, Orchestrator and AI. Never create client-specific models.
- Workflow files go through `WorkflowLoader` (parse → schema version → structure → semantics); it reports all
  diagnostics and never throws for bad input. Model constructors enforce invariants only.
- Activities implement `IActivity`, declare an `ActivityDescriptor` (properties by kind, children, slots) and are
  registered with `AddActivity<T>(descriptor)` (host) or `IPluginRegistrar.AddActivity<T>` (plugin). They use only
  `IActivityContext` — no engine internals, no service locator, no UI concerns.
- Activity contract (ADR-0013, frozen for SDK 1.0): one instance per node invocation, disposed by the engine; exactly
  one public constructor (dependencies are services); resources live in run- or plugin-lifetime services, never in
  activity fields; `ActivityResult.Completed` is the only outcome; classified failures throw `ActivityFailedException` /
  `AutomationException`; `SetValue` only for names from the node's assignment-target properties; cancellation is
  cooperative. Changing `IActivity`, `IActivityContext`, `ActivityResult` or anything in `MyRPA.Sdk` is an SDK
  version decision (ADR).
- Activity type names are `Namespace.Name` (built-ins `Core.*`) and are never CLR type names.
- Failures are exceptions; the engine attributes them to the node (`WorkflowActivityException`). Never swallow
  exceptions; cancellation (`OperationCanceledException` of the run token) must propagate.
- Expressions (ADR-0009) are the only computation language: no reflection, no CLR member access; new functions go
  into the `ExpressionFunctions` whitelist with tests.
- Values are canonical (`WorkflowValues`): null, string, long, decimal, bool, DateTimeOffset, read-only list,
  read-only string-keyed dictionary.
- Time and ids come from injected `TimeProvider` / `IIdGenerator`; never `DateTime.Now`, `Guid.NewGuid()` for ids, or
  `Thread.Sleep`.
- Correlate logs and spans with `IExecutionScopeFactory` and `DiagnosticNames` keys (ADR-0006, ADR-0010).
- Public async APIs take a `CancellationToken`; library code uses `ConfigureAwait(false)`.
- Public APIs in `src` have XML documentation (the build requires it).

## Web-first direction (ADR-0021 to ADR-0025)

- The Web Studio (React + TypeScript + Vite, `web/studio`) is the only Studio UI; `MyRPA.Server` is the control plane.
  The v1.0 JSON stays the only workflow format.
- The WPF Studio is archived in `archive/wpf-studio` (ADR-0036; tag `wpf-studio-final`): not built, not tested, never
  referenced, never a design constraint. Do not change it or restore it into the solution.
- Progress and events come from the per-run observer (`WorkflowRunRequest.Observer`, ADR-0023), never from span
  listeners; observer failures never change a run's outcome; events carry no workflow data. Hosts use
  `MyRPA.Execution.Hosting` (`ExecutionHost`) rather than calling the runner and capturing events themselves.
- `spikes/` holds throwaway measurement code: not in the solution, never referenced from `src`.
- Web Studio (`web/studio`, ADR-0028): the document model is the v1.0 JSON itself (immutable, client keys in a
  `WeakMap`, never saved); files that cannot be written back unchanged open read-only; one `EventSource` per tab; no
  state, UI, editor or drag-and-drop library (dnd-kit and other DnD frameworks fail `WebStudioRulesTests`).
  Structural edits and undo/redo follow ADR-0029: pure edits with refusal reasons, snapshot history (200 steps, typing
  merged), dirty = not the saved object.

## Studio conventions (Web Studio, ADR-0028 to ADR-0035)

- The Studio talks only to its own `MyRPA.Server` origin. Validation always goes through the server (`WorkflowLoader`);
  runs always go through `ExecutionHost`. The browser never executes workflows and never duplicates loader rules.
- Never add Studio-only activity knowledge: everything comes from the catalog (`ActivityDescriptor`).
- Edits are pure functions on the v1.0 JSON with refusal reasons (`document.ts`, `placement.ts`, `workflowData.ts`); the
  store is the small `useSyncExternalStore` store; diagnostics are located like WPF did (corpus parity, ADR-0035).
- Keep the CSP (`default-src 'self'`: no inline styles or scripts), the session, Origin and anti-forgery checks.

## Code style

- `.editorconfig` is authoritative: file-scoped namespaces, `_camelCase` private fields, braces required.
- Warnings are errors. Suppress an analyzer only locally, with a justification comment.
- Package versions live in `Directory.Packages.props`. Adding a package needs a reason and must respect the allow-lists.

## Tests

- xUnit v3 on Microsoft.Testing.Platform. Naming: `Subject_Condition_ExpectedResult`.
- Unit test projects reference only their subjects (Activities tests also reference Runtime to run the real engine);
  `MyRPA.Integration.Tests` composes the real CLI host and also runs the executable as a child process.
- Deterministic: use `FakeTimeProvider` and sequential id generators; pass `TestContext.Current.CancellationToken`;
  no network; no `Thread.Sleep`.
- When adding an architecture rule, also add a known-bad self-test for its detector.
- Shipped `samples/*.json` must stay valid (`CliWorkflowTests.ShippedSamples_AreValid`).
