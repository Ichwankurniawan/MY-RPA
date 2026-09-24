# MyRPA Architecture Overview

Status: current as of **Phase 4 — Browser Automation** (2026-09-24).
Why it looks like this: [phase-1-reconciliation.md](phase-1-reconciliation.md) and the [ADRs](../adr/README.md).
Details: [execution-model.md](execution-model.md) (engine), [workflow-format.md](workflow-format.md) (JSON format),
[automation-sdk.md](automation-sdk.md) (activity/provider contract), [plugin-system.md](plugin-system.md) (plugins),
[browser-automation.md](browser-automation.md) (Playwright browser plugin).
Evidence from the OpenRPA study: [../research/openrpa-analysis.md](../research/openrpa-analysis.md).

## 1. What exists

- A platform-neutral **Core** (identifiers, `IIdGenerator`, execution identity, diagnostic names, `ExecutionStatus`,
  activity metadata/schema contracts).
- The **single workflow model** with arguments, variables, typed property values and slots; a **versioned JSON format**
  with a multi-error **validation pipeline**; a constrained **expression language**; execution **contracts**.
- A deterministic, async **workflow engine** with cancellation, timeouts, error attribution, nested workflow
  invocation, per-run DI scopes, structured logging scopes and tracing spans.
- **12 built-in activities**: Sequence, Assign, Log, Delay, If, Switch, While, DoWhile, ForEach, TryCatch, Throw,
  InvokeWorkflow.
- File-based workflow loading with a confined resolver for `InvokeWorkflow`.
- The **Automation SDK** (SDK 1.0): the frozen activity contract (per-invocation lifetime, classified failures,
  cooperative cancellation, minimal context), the plugin contract (`IPlugin`, `IPluginRegistrar`), and
  technology-neutral provider, element and selector abstractions.
- The **plugin host**: manifests, explicit allow-listed discovery, compatibility/dependency/integrity checks,
  one collectible `AssemblyLoadContext` per plugin, the Discover → Validate → Load → Initialize → Register → Use →
  Dispose lifecycle with failure isolation, and a sample plugin (`Demo.Echo`, `Demo.GetField`, `Demo.Text` provider).
- The **browser automation plugin** `MyRPA.Browser.Playwright` (Playwright 1.63.0, Chromium): 11 `Browser.*`
  activities, run-lifetime browser sessions (one browser process per session, closed when the run ends), a provisional
  selector syntax over the SDK `Selector`, structured browser error types, a confined upload/download file policy, and
  no JavaScript evaluation. Plugin assemblies are now loaded from their verified path (ADR-0016).
- The `myrpa` CLI: `validate`, `run`, `info`, `plugins`, and the global `--plugin <directory>` option.
- Unit, integration (in-process and child-process) and architecture tests.

Not yet: Studio (5), recorder/selector engine (6), Windows and other enterprise automation (7), AI/MCP (8–9),
orchestrator/queues/triggers (10), RBAC/credentials (11), packages/signing (12).

## 2. Projects and responsibilities

| Project | Responsibility | Depends on | Packages |
|---|---|---|---|
| `MyRPA.Core` | Identifiers (`WorkflowId`, `NodeId`, `ExecutionId`, `CorrelationId`, `IIdGenerator`), `ExecutionIdentity`, `DiagnosticNames`, `ExecutionStatus`, activity metadata (`ActivityTypeName`, `ActivityDescriptor`, property/slot definitions, `IActivityCatalog`) | — | none |
| `MyRPA.Workflow` | Workflow model; values (`WorkflowValues`, `WorkflowDataType`); expressions; JSON reader/writer; `WorkflowLoader` validation pipeline; execution contracts (`IActivity`, `IActivityContext`, `IActivityFactory`, `IWorkflowRunner`, `IWorkflowResolver`, results, exceptions) | Core | none (BCL `System.Text.Json`) |
| `MyRPA.Activities` | `ActivityCatalog` (catalog + per-invocation factory), `ActivityRegistration`, `AddActivity<T>` registration, built-in `Core.*` activities | Core, Workflow | DI.Abstractions, Logging.Abstractions |
| `MyRPA.Runtime` | `WorkflowRunner` engine, `VariableScope`, `ActivityContext`, `TimeOrderedIdGenerator`, `WorkflowRuntimeOptions`, `MyRpaTelemetry`, `IExecutionScopeFactory`, `AddMyRpaRuntime` | Core, Workflow | DI.Abstractions, Logging.Abstractions |
| `MyRPA.Storage` | `WorkflowFileLoader`, `FileWorkflowResolver` (scoped); no database | Core, Workflow | DI.Abstractions |
| `MyRPA.Sdk` | Automation SDK: `AutomationSdk`/`SdkVersion`; plugin contract (`IPlugin`, `IPluginRegistrar`, `PluginContext`, `PluginId`, `PluginVersion`, `PluginServiceLifetime`); automation abstractions (`IAutomationProvider`, `IAutomationElement`, `Selector`, `ISelectorResolver`, `SelectorMatch`, `AutomationException`) | Core, Workflow | none |
| `MyRPA.Plugins` | Plugin host: `PluginManifestReader`, `PluginLoader`, `PluginLoadContext` (the only assembly loader), `PluginSet`/`IPluginRegistry`, `AddMyRpaPlugins` | Core, Workflow, Sdk, Activities | DI.Abstractions |
| `MyRPA.Cli` (`myrpa`) | Composition root: Generic Host, logging (stderr), plugin loading (`--plugin`), commands `info`, `validate`, `run`, `plugins` | all of the above | Hosting |

Plugins (outside `src`): `plugins/MyRPA.Browser.Playwright` (browser provider; the only project allowed to reference
`Microsoft.Playwright`), `samples/plugins/MyRPA.Samples.DemoPlugin` (sample) and `tests/fixtures/*` (test fixtures).
They reference only `MyRPA.Sdk` (plus their own technology packages) and are loaded exclusively through the plugin
host.

Tests: `MyRPA.{Core,Workflow,Runtime,Activities,Storage}.Tests` (unit; Runtime uses test-only activities, Activities
runs through the real engine and includes the concurrent-isolation regression test), `MyRPA.Sdk.Tests` (the frozen
activity contract through the real engine), `MyRPA.Plugins.Tests` (manifests, discovery, trust, lifecycle, isolation,
unloading, the sample plugin), `MyRPA.Browser.Playwright.Tests` (real headless Chromium against a local test site,
through the real plugin host), `MyRPA.Integration.Tests` (CLI in-process and as a child process, shipped samples,
`--plugin`), `MyRPA.Architecture.Tests` (rules below).

## 3. Dependency direction

```mermaid
graph BT
    Core[MyRPA.Core]
    Workflow[MyRPA.Workflow] --> Core
    Activities[MyRPA.Activities] --> Core
    Activities --> Workflow
    Runtime[MyRPA.Runtime] --> Core
    Runtime --> Workflow
    Storage[MyRPA.Storage] --> Core
    Storage --> Workflow
    Sdk[MyRPA.Sdk<br/>Automation SDK] --> Core
    Sdk --> Workflow
    Plugins[MyRPA.Plugins<br/>plugin host] --> Sdk
    Plugins --> Activities
    Cli[MyRPA.Cli<br/>composition root] --> Runtime
    Cli --> Activities
    Cli --> Storage
    Cli --> Plugins
    Cli --> Sdk
    PluginAsm[Plugin assemblies<br/>own AssemblyLoadContext] -.->|compile against| Sdk
```

- Core depends on nothing; Workflow depends only on Core (both BCL-only, plain `net10.0`).
- `Runtime` does **not** depend on `Activities`: the engine resolves activities through `IActivityFactory`, and its tests
  run with test-only activities.
- `Activities → Workflow` was added in Phase 2 ([ADR-0010](../adr/0010-workflow-execution-model.md) amends ADR-0003) because
  activities implement the execution contracts.
- `MyRPA.Sdk` and `MyRPA.Plugins` were added in Phase 3 ([ADR-0013](../adr/0013-automation-sdk-and-activity-contract.md)).
  The engine and built-in libraries (Core, Workflow, Activities, Runtime, Storage) never reference them; plugins
  reference only `MyRPA.Sdk` (which brings Core and Workflow) and are never referenced by anything.
- Only composition roots see the whole graph and reference `Microsoft.Extensions.Hosting`.

## 4. Architecture rules (enforced by `tests/MyRPA.Architecture.Tests`)

| Rule | Test |
|---|---|
| Every `src` project has a rule entry; project references stay in allow-lists; no cycles; nothing references a composition root; `src` never references tests | `ProjectGraphTests.*` |
| Runtime does not reference Activities | `ProjectGraphTests.Runtime_DoesNotDependOnActivityLibrary` |
| The engine and built-in libraries never reference the SDK or the plugin host | `ProjectGraphTests.Engine_NeverDependsOnThePluginSystem` |
| Plugin projects reference only `MyRPA.Sdk` and set `EnableDynamicLoading`; tests only build plugins (never compile against them) | `ProjectGraphTests.PluginProjects_*`, `TestProjects_BuildOnlyReferencesArePluginProjects` |
| Technology packages live only in their provider plugin (`Microsoft.Playwright` → `MyRPA.Browser.Playwright`) | `ProjectGraphTests.TechnologyPackages_AreReferencedOnlyByTheirPlugin` |
| Test projects reference only their subjects | `ProjectGraphTests.TestProjects_ReferenceOnlyTheirSubjects` |
| Plain `net10.0` (no `-windows`), no `UseWPF`/`UseWindowsForms`/`FrameworkReference` | `PlatformNeutralityTests.*` |
| Package allow-lists; Core/Workflow have no packages and reference only the BCL | `PlatformNeutralityTests.*` |
| No forbidden technology (WPF/WinForms/XAML, Playwright/Selenium/CEF/WebView2, FlaUI/UIA, WF4/CoreWF, DB drivers/ORMs, AI SDKs, MCP, messaging/ASP.NET Core) | `PlatformNeutralityTests.*` |
| Only composition roots reference Hosting and are executables | `PlatformNeutralityTests.OnlyCompositionRoots_*` |
| No `async void`; no mutable static fields | `CodeRuleTests.*` |
| IL scan bans `Type.GetType(string)`, `Assembly.Load*`, `Assembly.GetType(string)`, `AppDomain.Load*/CreateInstance*`, `AssemblyLoadContext.LoadFrom*`, `Activator.CreateInstance(string…)`, `BinaryFormatter`, `Process.Start`, `HttpClient`/`WebClient`/`WebRequest`, sockets | `CodeRuleTests.NoBannedApiCalls` |
| The only exemption: `PluginLoadContext` may load assemblies and find the manifest's entry type; the exemption is used and does not leak | `CodeRuleTests.BannedApiExemptions_*`, `Exemptions_DoNotApplyToOtherTypes` |
| Plugin documentation states that `AssemblyLoadContext` is not a security boundary | `DocumentationTests.*` |

Every detector is self-tested against known-bad samples, and rules were verified by injecting real violations.

## 5. Composition (CLI)

```text
myrpa [--verbose] [--plugin <dir>]... <args>
  → CliApplication.RunAsync
      plugins (only if --plugin given): PluginLoader.LoadAsync → diagnostics to stderr → exit 5 if any failed
  → Host.CreateApplicationBuilder(ApplicationName = "myrpa", ContentRoot = install dir)
      logging: SimpleConsole → stderr; Warning (default) / Debug (--verbose); MyRPA.Workflow.Log at Information
      services: AddMyRpaRuntime + AddMyRpaActivities + AddMyRpaStorage + commands (info, validate, run, plugins)
                + AddMyRpaPlugins(plugins)
      container: ValidateOnBuild + ValidateScopes
  → dispatcher → command (token linked to Ctrl+C) → exit code
  → host disposed (plugin services) → plugins disposed and unloaded
```

Exit codes: `0` success, `1` workflow failed, `2` usage/file problem, `3` invalid workflow, `4` timed out,
`5` plugin failed to load, `130` cancelled.
`run` prints the execution result as JSON on stdout; logs never go to stdout.

## 6. Observability

`ExecutionIdentity` (execution, correlation, workflow, node, parent execution) flows into both `ILogger` scopes and
`ActivitySource("MyRPA.Runtime")` span tags; spans record outcome, status and exceptions. See
[execution-model.md §5](execution-model.md#5-execution-context-and-observability).

## 7. Security baseline

[ADR-0008](../adr/0008-secure-by-default-baseline.md) + [ADR-0012](../adr/0012-invoke-workflow-resolution-and-limits.md)
+ [ADR-0015](../adr/0015-plugin-trust-model.md):
no network listeners or calls, no process execution, no dynamic code or type-name resolution in MyRPA's own code, no
secrets in workflows; expressions cannot reach .NET members; InvokeWorkflow is confined to the entry workflow's
directory with depth limits; configuration is read from the install directory, not the caller's working directory.

Plugins are the one deliberate way to add code. They are loaded only from directories the operator names, validated
before any of their code runs, optionally pinned by SHA-256, and every assembly is re-verified at load. In-process
plugins are **fully trusted**: `AssemblyLoadContext` is not a security boundary, and declared capabilities are not
enforced. Untrusted plugins need process isolation (future phases).

## 8. Phase 4 outcome and handoff to Phase 5 (not started)

- Browser automation was delivered as a plugin exactly as planned, **without SDK changes**. The one host change was
  verified path-based assembly loading (ADR-0016), because Playwright locates its driver next to its assembly.
- Sessions are referenced by id strings (`browser-1`, …) held in workflow variables, with an implicit default when
  exactly one session is open (ADR-0017).
- For Studio (Phase 5): activity metadata (descriptors with property kinds, allowed values and descriptions) is available
  from `IActivityCatalog` for built-ins and plugins alike; `IPluginRegistry` lists plugins. Studio must load plugins
  through the same plugin host and must be a non-AOT host (ADR-0016).
- Open question for Phase 6: the recorder and Studio will need browser contracts visible outside the plugin (shared
  technology-contract assemblies, Phase 3 review item I2).
