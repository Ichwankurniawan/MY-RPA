# OpenRPA — Analysis: What MyRPA Should Learn

**Phase:** 0 — research only. This document identifies lessons and opportunities; it does **not** define
the MyRPA architecture (that is Phase 1+ with ADRs).
Evidence references point to `reference/openrpa/` @ `b78115e` and to the other research documents.

---

## 1. Concepts Worth Retaining

Each concept below is confirmed in source.

| # | Concept | Evidence | Why it is valuable |
|---|---|---|---|
| R1 | **Workflow definition vs. workflow instance** separation, instance carries parameters, state, bookmarks, owner/host, error, outputs | `Workflow` / `WorkflowInstance` (`OpenRPA/Workflow.cs`, `OpenRPA/WorkflowInstance.cs`) | Clean model for run history, remote invocation, and persistence |
| R2 | **Typed workflow arguments with direction** (In / Out / InOut) derived from the definition and used for remote invocation mapping | `Workflow.ParseParameters`, `workflowparameter`, `RobotInstance.WebSocketClient_OnQueueMessage` param mapping | Same contract for CLI, Studio, Robot, Orchestrator (PRD 7.3) |
| R3 | **Composite activity model** (sequence/flowchart/loops/try-catch) with variables in scopes and bookmarks for waiting | WF4 usage throughout; `BreakableLoop`, `Detector`, `InvokeOpenRPA` bookmarks | Proven expressive model; bookmarks enable human-in-the-loop and event waits (PRD 9.5) |
| R4 | **Finder activity with scoped body** (`GetElement` → `item` → `ClickElement`/`Assign item.Value`), `MinResults/MaxResults/Timeout`, `Index/Total` | `OpenRPA.Windows/Activities/GetElement.cs`, `OpenRPA/Activities/ClickElement.cs` | Readable, recordable, handles "for each matching element" naturally |
| R5 | **Provider-neutral element contract** so generic activities work on desktop and web | `IElement` (`OpenRPA.Interfaces/IElement.cs`), `UIElement`, `NMElement` | Directly matches PRD `IAutomationElement` |
| R6 | **Virtual vs physical interaction** chosen per action (UIA pattern / DOM event vs. mouse input) | `UIElement.Click`, `NMElement.Click` | Reliability + fallback when patterns are unsupported |
| R7 | **Selector = serializable, technology-discriminated path of property bags**, per-property enable/disable, wildcards, `{{var}}` substitution, anchors | `Selector`, `SelectorItem`, `WindowsSelector`, `NMSelector` | Human-editable, diffable, recordable |
| R8 | **Selector uniqueness minimization** at generation time | `WindowsSelectorItem.EnumNeededProperties` | Produces short, robust selectors |
| R9 | **Recorder chain-of-responsibility**: base hit-test + technology recorders claim events by priority | `MainWindow.OnUserAction` → `IRecordPlugin.ParseUserAction` | Composes desktop, browser, SAP, Java recording |
| R10 | **Extension-point categories**: provider/recorder, trigger/detector, run-lifecycle hooks (with veto), storage, snippets/templates, per-run extensions | `OpenRPA.Interfaces/I*Plugin.cs`, `ICustomWorkflowExtension`, `ISnippet` | Maps onto PRD Phase 3 plugin categories |
| R11 | **Per-run services** created with the instance and disposed with it | `WindowsCacheExtension`, `Plugins.WorkflowExtensionsTypes` in `createApp` | Correct lifetime for UIA sessions, browser sessions, caches |
| R12 | **Tracking stream** as single source for debugger highlighting, logs, spans, activity metrics | `WorkflowTrackingParticipant` | One execution-event bus feeding Studio, logs, OTel |
| R13 | **Robot = queue; role/pool queues; competing consumers; "busy" decline** | `RobotInstance.RegisterQueues`, `e.isBusy` | Simple, scalable dispatch semantics (PRD 10.1-10.4) |
| R14 | **Request/response over messaging** with `replyto` + `correlationId`, and remote-invoke activities that wait on a bookmark | `InvokeRemoteOpenRPA`, `IdleOrComplete` reply | Enables robot→robot and workflow→orchestrator calls |
| R15 | **Work items** with payload, files, priority, retries, next-run, success/failed queue chaining, business-rule vs system errors | `IWorkitem`, `IWorkitemQueue`, `Workitems/*`, `BusinessRuleException` | Matches PRD queue requirements; business/system error split is important |
| R16 | **Detectors** as first-class, configurable trigger entities that can both resume waiting workflows and publish events | `Detector`, `IDetectorPlugin`, `MainWindow.OnDetector` | Event-based scheduling (PRD 10.5) |
| R17 | **End-to-end trace context** (`traceId/spanId`) from orchestrator message to workflow and activity spans | `RobotCommand.traceId/spanId`, `WorkflowTrackingParticipant` | Observability across process boundaries (PRD §20) |
| R18 | **Offline-first local store with sync** to the server | `LocallyCached.Save<T>`, `StorageProvider`, `IStorage` impls | Robots keep working when disconnected |
| R19 | **Out-of-process bridges** for SDKs with conflicting runtime/bitness | `OpenRPA.SAPBridge`, `OpenRPA.JavaBridge`, `NativeMessagingHost` | Needed pattern for SAP/Java/legacy COM on .NET 10 |

---

## 2. Concepts That Should Be Redesigned

### D1. Workflow engine

- **OpenRPA approach:** Hosts Windows Workflow Foundation 4 (`System.Activities.WorkflowApplication`) with XAML
  definitions and compiled VB expressions (`Workflow.Activity()`, `CompileExpressions = true`).
- **Problem / limitation:** WF4 is .NET Framework-only (not on .NET 10). XAML + VB expressions are hard to diff, generate,
  validate, or produce with AI; expression compilation is slow and a code-execution surface. Engine internals are
  reached via reflection (`WorkflowInstance.DoStuff` reads private fields `executor`, `firstWorkItem`; `Abort` reads
  private `state`).
- **MyRPA proposed direction:** Own, small, deterministic engine over a versioned JSON workflow model (PRD Phase 2),
  with an explicit, sandboxable expression strategy; bookmark/idle semantics retained conceptually.
- **Reason:** PRD targets .NET 10 and JSON; one engine for CLI/Studio/Robot (PRD 7.3); AI-generated workflows need
  schema validation (PRD 8.3).

### D2. Activity contract

- **OpenRPA approach:** Activities are WF4 classes with WPF designer attributes, VB default expressions, and defaults
  read from global `Config.local` in constructors.
- **Problem:** Activity definition, runtime behavior, designer UI, and configuration are fused; no async/cancellation;
  blocking `Thread.Sleep` (e.g. `ClickElement.PostWait`).
- **Direction:** Activity = pure runtime type + declarative metadata (schema, category, display) + separate designer
  contribution; `async` execution with `CancellationToken`, timeouts, and structured results.
- **Reason:** Core independence from UI (PRD 7.1), testability, cancellation/timeout requirements (PRD 2.5).

### D3. Plugin discovery, isolation, lifecycle

- **OpenRPA approach:** `Assembly.Load` every DLL in the exe folder and `extensions`, reflect all types in the AppDomain,
  `Activator.CreateInstance`; substring deny-list; no unload/dispose (`Plugins.LoadPlugins`).
- **Problem:** Any DLL dropped in the folder executes; dependency conflicts are global; startup cost grows with all types;
  host refers to plugins by name strings; no versioned host API.
- **Direction:** Manifest-based discovery, per-plugin `AssemblyLoadContext`, explicit `Register(IPluginRegistry)`,
  DI-scoped services, ordered lifecycle Discover → Load → Initialize → Register → Execute → Dispose (PRD 3.2),
  optional signature/hash validation.
- **Reason:** Security, stability, and PRD Phase 3 definition of done.

### D4. `IRecordPlugin` monolith

- **OpenRPA approach:** One interface for recording, element tree browsing, selector generation, selector resolution,
  launch/close application, and a WPF editor.
- **Problem:** Providers without recording (Office) must stub everything; impossible to run headless without WPF.
- **Direction:** Separate capabilities: `IAutomationProvider`, `ISelectorResolver`, `ISelectorGenerator`, `IRecorder`,
  `IElementTreeBrowser`, Studio-only UI contributions.
- **Reason:** Interface segregation; matches PRD 3.1 abstraction list.

### D5. Contracts assembly

- **OpenRPA approach:** `OpenRPA.Interfaces` contains contracts **and** FlaUI `UIElement`, NLog, WPF views, input hooks,
  .NET Remoting service, registry/DPAPI config, OpenFlow entities.
- **Problem:** Every plugin transitively depends on Windows/WPF/COM; SDK cannot be platform-neutral.
- **Direction:** Thin, platform-neutral SDK (`netX.0`) with only abstractions; technology implementations in provider
  packages (`net10.0-windows` only where required).
- **Reason:** PRD 7.1 / 10.1 core independence.

### D6. Global mutable state and startup

- **OpenRPA approach:** Singletons and statics (`RobotInstance.instance`, `global.*`, `Plugins.*`, `Config.local`,
  `WorkflowInstance.Instances`, `NMHook`), side-effects in singleton getter, `async void` handlers, UI-thread marshaling
  helpers everywhere, `Environment.Exit(1)` on lock timeouts.
- **Problem:** Hard to test, order-dependent, race-prone, impossible to host multiple runtimes.
- **Direction:** Generic Host + DI + hosted services; explicit state stores; no UI-thread coupling in runtime.
- **Reason:** PRD §26 (no god classes / global mutable state / hidden dependencies).

### D7. Runtime ↔ UI coupling (debugging)

- **OpenRPA approach:** Breakpoints block the workflow thread on an `AutoResetEvent` owned by the WPF designer
  (`WFDesigner.OnVisualTracking`); run is initiated through designer/UI objects; remote invoke also routes via designer
  when a workflow is open.
- **Problem:** Runtime cannot run headless with debugging; UI state affects execution.
- **Direction:** Runtime exposes a debugging/control API (pause, step, resume, breakpoints by node id) and an event stream;
  Studio is one client.
- **Reason:** Same engine for CLI/Studio/Robot (PRD 7.3, 5.4).

### D8. Selector resolution policy

- **OpenRPA approach:** Retry/timeout loops, cache clears, STA thread switching are re-implemented in each finder
  activity; resolution has side effects (restores minimized windows, focuses for mouse-over search).
- **Problem:** Inconsistent behavior across providers; hidden side effects.
- **Direction:** Shared resolver service with a uniform wait/retry policy and explicit, opt-in side effects; typed,
  versioned selector schema with multiple strategies and a fallback chain (PRD 6.1-6.2).
- **Reason:** Determinism (PRD 2.1) and testability.

### D9. Browser automation stack

- **OpenRPA approach:** MV2 extension → native messaging host exe → per-session named pipe → `NMHook` statics; JSON
  messages; screen-coordinate arithmetic; registry writes to register the host.
- **Problem:** Many moving parts and processes, MV2 deprecation, DPI/offset hacks, custom protocol, extension install
  required, remote code loading (see N2).
- **Direction:** Playwright provider (PRD Phase 4) with sessions (Browser/Context/Page) as per-run services;
  locator-based selectors; recording via in-page instrumentation controlled by Playwright.
- **Reason:** PRD technology choice; reliability (auto-wait) and security.

### D10. Orchestration protocol

- **OpenRPA approach:** Bespoke JSON-over-WebSocket protocol; robots have generic DB CRUD (`Query/InsertOne/UpdateOne/DeleteOne`
  against named collections such as `openrpa`, `openrpa_instances`, `users`, `mq`) and message-queue primitives; job semantics partly client-side (busy counting, kill flags).
- **Problem:** Broad robot privileges; server/robot contract implicit; hard to version and secure.
- **Direction:** API-first orchestrator with typed endpoints and a robot job/lease protocol (pull or push),
  server-owned scheduling/queues/retries, least-privilege robot identity.
- **Reason:** PRD Phase 10/11 (RBAC, audit, security).

### D11. Persistence and state

- **OpenRPA approach:** `StorageProvider` writes to **all** registered stores and reads from the first non-empty;
  WF4 instance state serialized Base64 into the instance entity; resume-after-restart is commented out
  (`WorkflowInstance.RunPendingInstances`).
- **Problem:** Ambiguous source of truth; durable execution not actually working.
- **Direction:** Single repository abstraction per concern (definitions, run history, checkpoints) with explicit
  migrations; checkpointing designed in (or explicitly out) of the Phase 2 engine.
- **Reason:** PRD 10.6 workflow versioning / migrations.

### D12. Configuration

- **OpenRPA approach:** `settings.json` in one of several directories + HKLM/HKCU registry overrides + per-plugin
  namespaces, all via `Config.local` static.
- **Problem:** Location ambiguity, Windows-only, no environments.
- **Direction:** `Microsoft.Extensions.Configuration` layering (files, env vars, command line), typed options per
  provider, environment profiles (PRD §24).
- **Reason:** Cross-platform core, dev/test/prod separation.

### D13. Logging

- **OpenRPA approach:** Static `Log` → `Trace` + NLog file with concatenated strings; function-indent pseudo stack;
  `Tracing` listener maps thread-local instance id to logs.
- **Problem:** Unstructured, global, thread-local correlation breaks with async.
- **Direction:** `Microsoft.Extensions.Logging` with scopes (execution id, workflow id, node id, robot id), OpenTelemetry
  for traces/metrics/logs (OpenRPA already proves the OTel value).
- **Reason:** PRD §8, §20.

### D14. Package management

- **OpenRPA approach:** NuGet client downloads project dependencies for `net462` into one shared `extensions` folder
  and loads DLLs into the host AppDomain.
- **Problem:** Cross-project version clashes, no lock file, no isolation, no validation.
- **Direction:** Workflow packages with manifest, dependency lock, per-package load context, validation/signing,
  local/private registries (PRD Phase 12).
- **Reason:** Reproducibility and security.

---

## 3. Concepts That Should Not Be Copied

| # | Implementation detail | Evidence | Why not |
|---|---|---|---|
| N1 | **.NET Remoting IPC with `BinaryServerFormatterSinkProvider { TypeFilterLevel = Full }`** exposed to BUILTIN\Users | `OpenRPA.Interfaces/IPCService/OpenRPAService.cs:55-80` | BinaryFormatter/Remoting deserialization is a known RCE class; any local user can invoke workflows; not available on .NET 10 |
| N2 | **Browser extension `eval`s JavaScript received from the native host** at runtime (`backgroundscript`, `loadscript`) | `OpenRPA.NativeMessagingHost/addon/background.js`, `addon/content.js` | Remote code execution inside every page/frame with `<all_urls>`; defeats store review; blocked by MV3 |
| N3 | Loading **every DLL** in app folder / `extensions` / `%TEMP%` (AssemblyResolve probes `Path.GetTempPath()`) | `Plugins.LoadPlugins`, `App.LoadFromSameFolder` | DLL planting / hijacking risk |
| N4 | `remote_allowed` default **true**; default `wsurl = wss://app.openiap.io/` | `OpenRPA.Interfaces/Config.cs:18, 65` | Secure-by-default violated; unexpected external connection |
| N5 | Plain-text `unsafepassword` in settings (converted later) | `Config.unsafepassword`, `RobotInstance.cs:1163` | Credentials at rest in clear text (PRD 11.3) |
| N6 | Remote parameter typing via `Type.GetType(p.type)` + `ToObject` on untrusted input | `RobotInstance.WebSocketClient_OnQueueMessage` | Type confusion / gadget risk; should be schema-validated |
| N7 | Reflection into private WF4 internals (`executor`, `firstWorkItem`, `state`) | `WorkflowInstance.DoStuff`, `Abort` | Brittle, unsupported |
| N8 | Swallow-all `catch (Exception) { }` and `Environment.Exit(1)` on lock timeouts | pervasive; `WorkflowInstance.Create`, `RobotInstance.WorkflowInstances` | Hides failures; abrupt termination loses state |
| N9 | Host hard-codes plugin names (`"Windows"`, `"SAP"`, `"OpenRPA.Script"`) | `MainWindow.StartRecordPlugins`, `WFToolbox.InitializeActivitiesToolbox` | Breaks plugin independence |
| N10 | DPI/window offset magic numbers (`uiy += 158`, `uix -= 7`) | `OpenRPA.NM/Plugin.cs OnMessage`, `NMHook.Client_OnReceivedMessage` | Non-deterministic across machines |
| N11 | Toolbox built from "every public activity type in the AppDomain" + deny-list | `WFToolbox.InitializeActivitiesToolbox` | Accidental exposure; ordering/names uncontrolled |
| N12 | Suppressing the user's click during recording and re-injecting it | `MainWindow.OnRecord` (`CallNext = false`) + replay in `OnUserAction` | Timing/focus bugs; confusing UX |
| N13 | Robots holding generic DB CRUD rights to server collections | Robot host code calls `webSocketClient.Query` (26×), `DeleteOne` (5×), `InsertOne` (3×), `InsertOrUpdateOne` (2×), `UpdateOne` (2×) across `OpenRPA/*.cs`, `Activities/`, `Views/` (e.g. `RobotInstance.LoadServerData`, `LocallyCached.Save<T>`) | Violates least privilege |
| N14 | Copying source code | License MPL-2.0 (file-level copyleft) | PRD §30: learn concepts, do not reproduce implementation |

---

## 4. Modernization Opportunities

| Area | Observation in OpenRPA | Opportunity for MyRPA |
|---|---|---|
| **Modern .NET** | .NET Framework 4.6.2, WF4, Remoting, `System.Activities.Presentation` | .NET 10: `async`/`CancellationToken` everywhere, `System.Text.Json` source-gen for workflow schema, `AssemblyLoadContext`, Generic Host, `TimeProvider` for testable timeouts, NativeAOT for small tools (CLI) where feasible |
| **Dependency boundaries** | Contracts assembly depends on WPF/FlaUI/NLog/Remoting | Platform-neutral Core/SDK; `-windows` TFMs only in Windows provider and Studio; enforce with architecture tests (no Core → WPF/Playwright/UIA references) |
| **Playwright** | Custom extension + native host + pipes | Playwright .NET provider: Browser/Context/Page sessions, locators (role/text/test-id/css/xpath), auto-wait, tracing/screenshots for failure evidence, downloads/uploads, `codegen`-style recording |
| **Cross-provider abstractions** | `IElement` + per-tech `Selector` subclass | `IAutomationProvider` / `IAutomationElement` / `ISelector` / `ISelectorResolver` with capability flags (Invoke, SetValue, Text, Screenshot) so activities can degrade gracefully |
| **Modern plugin architecture** | Reflection scan, no isolation/unload | Manifest + `AssemblyLoadContext` + DI registration + versioned host API; out-of-proc option (gRPC/named pipes) for SAP/Java/COM bridges |
| **AI** | None (only Rossum document AI plugin) | Because workflows become JSON with a schema, AI can generate/explain/repair workflows under schema + policy validation (PRD 8.3); recorder output + selector alternatives are good AI inputs for self-healing selectors (future, not Phase 1) |
| **MCP** | None | Expose MyRPA workflows/activities as MCP tools and consume MCP servers from agent activities, behind a policy engine (PRD Phase 9) |
| **API-first orchestration** | WebSocket JSON protocol with generic CRUD | Typed REST/gRPC orchestrator API, OpenAPI contract, robot lease/heartbeat protocol, server-side scheduler/queues; same workflow package format for local and remote |
| **Observability** | OTel spans per workflow/activity, counters, histograms — genuinely good | Keep and standardize: `ActivitySource` per engine/provider, semantic attributes (workflow.id, execution.id, node.id, robot.id), structured logs correlated to traces; failure artifacts (screenshots, Playwright traces) |
| **Security** | See §3 N1–N6, N13 | Secure defaults, credential references resolved at runtime (DPAPI/Windows Credential Manager locally, vault server-side), signed packages, least-privilege robot tokens, audit events, domain allowlists for browser/AI tools |
| **Testing** | No test projects | Engine unit tests over JSON workflows; provider contract tests (same test suite runs against each `IAutomationProvider`); Playwright tests against a local test site; architecture tests for dependency rules |
| **Package management** | Shared `extensions` folder, `net462` NuGet | Workflow/plugin packages with manifest + lock file, per-package isolation, registry (local/private/public), promotion across environments |

---

## 5. Validation — answers from source

| # | Question | Answer (evidence) |
|---|---|---|
| 1 | What happens when OpenRPA starts? | `App.Main` single-instance → `App()` culture + `AssemblyResolve` → `Application_Startup` picks Main/Agent window, `RobotInstance.instance` (OTel, IPC server), `Plugins.LoadPlugins`, `RobotInstance.Initialize` (local entities), `init()` connects to OpenFlow or runs offline → `CreateMainWindow` + detectors → `ReadyForAction` → input hooks, command-line workflow. [runtime §1] |
| 2 | How are plugins discovered? | File scan of exe folder + `Documents\OpenRPA\extensions` for `*.dll`, substring deny-list, then reflection over all AppDomain types for the plugin interfaces. `Plugins.LoadPlugins` [plugins §2] |
| 3 | How are plugins loaded? | `Assembly.Load(AssemblyName.GetAssemblyName(dll))` into the default AppDomain; `Activator.CreateInstance` + `Initialize(IOpenRPAClient)`; detector types instantiated per `Detector` entity; workflow extensions per instance. |
| 4 | How are activities registered? | Implicitly: any public WF4 activity type in a loaded assembly is added to the toolbox by `WFToolbox.InitializeActivitiesToolbox` reflection (+ deny-list). Recorders construct activities directly. |
| 5 | How does the designer represent activities? | WF4 rehosted `WorkflowDesigner`; `ModelItem` tree edited via `ModelService`; per-activity WPF designers via `[Designer]`; persisted as XAML (`WorkflowDesigner.Text` → `Workflow.Xaml`). |
| 6 | What happens when the user presses Run? | `MainWindow.OnPlay` → `WFDesigner.Run` (flush, ACL `invoke` check, breakpoints) → `Workflow.CreateInstance` → `WorkflowInstance.Create` → `instance.Run()` → `wfApp.Run()`. [runtime §3] |
| 7 | How is a workflow instance created? | `WorkflowInstance.Create`: run-plugin veto, owner/host stamping, add to `Instances`, `createApp(Workflow.Activity())` → `new WorkflowApplication(activity, Parameters)` + tracking participant + custom extensions + instance store + handlers. |
| 8 | How does an activity execute? | WF4 scheduler calls `Execute(context)`; arguments via `InArgument.Get(context)`; finders schedule their `Body` with `ScheduleAction<T>(Body, element)`; async activities via `AsyncTaskCodeActivity`; waits via bookmarks. [activities §3] |
| 9 | How does an activity access an automation provider? | Two paths: (a) finder activities instantiate the provider's selector class and call its static resolver (`WindowsSelector.GetElementsWithuiSelector`, `NMSelector.GetElementsWithuiSelector`), using per-run extensions (`context.GetExtension<WindowsCacheExtension>()`); (b) generic activities call methods on the `IElement` they received as `item` (`UIElement`/`NMElement`). There is no provider registry lookup at execution time. |
| 10 | How are automation elements represented? | `IElement` (RawElement, Rectangle, Value, Name, Focus, Refresh, Click, Highlight, ImageString, Items); `UIElement` wraps FlaUI/managed UIA `AutomationElement`; `NMElement` wraps a native-messaging result (xpath, css, zn_id, rect). |
| 11 | How does selector resolution work? | JSON array → selector subclass by root `Selector` value; Windows: walk UIA tree step by step from desktop/anchor, filter by `Match` with wildcard properties, optional cache/mouse-over fallback; Browser: send `getelements` with xpath/css to page script. Activity loops until found or timeout, then `ElementNotFoundException`. [selectors §3-4] |
| 12 | How does recording work? | LL mouse hook → Windows recorder hit-tests via UIA, builds `WindowsSelector` + `GetElement`; host offers the event to other recorders by priority (`ParseUserAction`); host adds `ClickElement`/`Assign item.Value` body; inserts into designer via `AddRecordingActivity`; replays click if needed. [recording] |
| 13 | How does browser automation work? | `NM.*` activities → `NMSelector`/`NMElement` → `NMHook` → named pipe → `OpenRPA.NativeMessagingHost.exe` → stdio native messaging → extension background → content script `openrpautil[functionName]`; responses matched by `messageid`. [browser] |
| 14 | How does Windows automation work? | FlaUI UIA3 (`UIA3Automation`) tree walking + property matching; actions via UIA patterns (Invoke, Value, LegacyIAccessible) or physical input (`InputDriver`, FlaUI Keyboard). [windows] |
| 15 | How does native messaging work? | Browser starts host via registry-registered manifest `com.openrpa.msg`; 4-byte length + UTF-8 JSON on stdio; host exposes pipe `"{SessionId}_openrpa_nativebridge_{browser}"` using length-prefixed JSON frames; host serves JS bundles that the extension `eval`s. [browser §2] |
| 16 | How do events/detectors work? | `Detector` entities bind to `IDetectorPlugin` types; plugin raises `OnDetector` → `MainWindow.OnDetector` resumes bookmark `detector_<id>` in waiting instances and publishes `RobotCommand{detector}` to the detector's exchange/queue. [orchestration §7] |
| 17 | How does orchestration work? | OpenFlow WebSocket client; robot registers user-id and role queues; `queuemessage` with `RobotCommand{invoke}` → create/run instance → reply `invokesuccess` and later `invoke<state>` with `correlationId`; instance history synced to `openrpa_instances`; work items via `popworkitem/updateworkitem`. [orchestration] |
| 18 | How are errors handled? | Activities throw (`ElementNotFoundException`, `BusinessRuleException`, ...); WF4 `TryCatch` or `OnUnhandledException` → Terminate; instance records `state`, `errormessage`, `errorsource` (activity id), `Exception`; run plugins notified; remote caller receives `invokefailed/aborted` with exception JSON. Host code widely swallows exceptions into `Log.Error`. |
| 19 | How is logging implemented? | Static `Log` → `System.Diagnostics.Trace` + NLog file `logfile.txt`; `Tracing : TraceListener` routes to UI, instance console, OTel logger; OTel spans/metrics via `WorkflowTrackingParticipant` and `InitializeOTEL`. |
| 20 | What architectural concepts should MyRPA retain? | §1 above (R1–R19). |

---

## 6. Open questions to carry into Phase 1

1. Engine persistence scope: does Phase 2 need durable checkpoint/resume (OpenRPA's is effectively disabled), or only
   in-process bookmarks for waits/approvals? (Affects workflow model and storage design.)
2. Expression language for JSON workflows (none / safe expression evaluator / C# scripting) — security vs. power trade-off;
   OpenRPA relied on compiled VB.
3. Studio designer technology: WF4 rehosted designer is unavailable on .NET 10; the Studio canvas must be built (PRD Phase 5).
4. Whether attaching to a user's already-open browser is a requirement (the only capability the extension model gives that
   plain Playwright launch does not; `ConnectOverCDP` is a candidate).
5. How far to go with out-of-process providers from day one (SAP/Java/COM bridges proved necessary in OpenRPA).
