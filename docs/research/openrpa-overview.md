# OpenRPA — Architecture Overview

**Phase:** 0 — OpenRPA Reverse Engineering
**Source:** `reference/openrpa` @ `b78115e45bcfdc1a22398662bac355fdd52fac87` (2025-06-03), MPL-2.0
**Method:** Code-level reading of the C# sources. All paths below are relative to `reference/openrpa/`.
Line numbers refer to that commit.

## Document index

| Document | Covers |
|---|---|
| [openrpa-projects.md](openrpa-projects.md) | Solution structure, project graph, frameworks, NuGet, entry points |
| [openrpa-runtime.md](openrpa-runtime.md) | Startup, Run → WorkflowApplication, tracking, persistence, errors, logging, config |
| [openrpa-activities.md](openrpa-activities.md) | Activity model on System.Activities (WF4), designer, toolbox, arguments |
| [openrpa-plugins.md](openrpa-plugins.md) | Plugin discovery/loading, plugin interfaces, packages (NuGet) |
| [openrpa-recording.md](openrpa-recording.md) | Recorder pipeline, from mouse hook to designer insertion |
| [openrpa-selectors.md](openrpa-selectors.md) | Selector model, JSON form, generation, resolution, matching |
| [openrpa-browser.md](openrpa-browser.md) | Browser extension, native messaging host, named pipes, NM plugin |
| [openrpa-windows.md](openrpa-windows.md) | UI Automation provider (FlaUI UIA3), `UIElement`, input driver, Office |
| [openrpa-orchestration.md](openrpa-orchestration.md) | OpenFlow WebSocket, robot queues, work items, detectors, remote run |
| [openrpa-analysis.md](openrpa-analysis.md) | What MyRPA should keep, redesign, avoid, and modernize |

## Summary in one paragraph

Observed: OpenRPA is a single **WPF desktop process** (`OpenRPA.exe`, .NET Framework 4.6.2) that
hosts the **Windows Workflow Foundation 4** (`System.Activities`) runtime *and* the WF4 rehosted
designer in the same process. Workflows are stored as XAML strings. Automation technologies (Windows
UIA, browsers, Office, SAP, Java, IE, images, scripts) are **plugin assemblies** that are copied next
to the exe, loaded via `Assembly.Load`, and scanned by reflection for a handful of interfaces
(`IRecordPlugin`, `IDetectorPlugin`, `IRunPlugin`, `IStorage`, `ISnippet`, `ICustomWorkflowExtension`).
Activities are ordinary WF4 activity classes discovered by reflecting over every loaded assembly.
The "automation provider" boundary is the `IElement` interface plus per-technology `Selector`
subclasses; activities like `ClickElement` call `IElement.Click(...)` and polymorphism dispatches to
`UIElement` (UIA/FlaUI) or `NMElement` (browser via native messaging). Central orchestration is
**OpenFlow** (a separate Node.js/MongoDB/RabbitMQ product, not in this repo), reached through a
custom JSON-over-WebSocket protocol (`OpenRPA.Net.WebSocketClient`); robots register a message
queue and receive `invoke` commands. Persistence is pluggable (`LiteDB` or file-system JSON) and
mirrored to OpenFlow when connected.

## Architecture map

```text
OpenRPA  (OpenRPA.exe, WPF, net462)                        [OpenRPA/]
│
├── Application
│   ├── App.Main / App.Application_Startup                 OpenRPA/App.xaml.cs
│   ├── RobotInstance (singleton, IOpenRPAClient)          OpenRPA/RobotInstance.cs
│   ├── MainWindow (IMainWindow) / AgentWindow             OpenRPA/MainWindow.xaml.cs, AgentWindow.xaml.cs
│   └── OpenRPAServiceUtil / OpenRPAService (.NET Remoting IPC, cmdline run)
│                                                          OpenRPA.Interfaces/IPCService/OpenRPAService.cs
├── Workflow Engine  = System.Activities (WF4) hosted via WorkflowApplication
│   ├── Workflow (IWorkflow, XAML + params)                OpenRPA/Workflow.cs
│   ├── WorkflowInstance (IWorkflowInstance, wraps WorkflowApplication)
│   │                                                      OpenRPA/WorkflowInstance.cs
│   ├── WorkflowTrackingParticipant (tracking → debug/OTel) OpenRPA/WorkflowTrackingParticipant.cs
│   └── OpenFlowInstanceStore (WF4 persistence)            OpenRPA/Store/*.cs
│
├── Activities (WF4 CodeActivity / NativeActivity / AsyncCodeActivity subclasses)
│   ├── Core: ClickElement, TypeText, OpenApplication, InvokeOpenRPA, InvokeRemoteOpenRPA,
│   │         InvokeOpenFlow, Detector, Workitems/*        OpenRPA/Activities/
│   ├── Bases: BreakableLoop, AsyncTaskCodeActivity, AsyncNativeActivity
│   │                                                      OpenRPA.Interfaces/
│   └── Per-plugin activities (Windows.GetElement, NM.GetElement, Office.*, Script.InvokeCode, ...)
│
├── Designer (WF4 rehosted WorkflowDesigner)               OpenRPA/Views/WFDesigner.xaml.cs
│   └── Toolbox (reflection over loaded assemblies)        OpenRPA/Views/wfToolbox.xaml.cs
│
├── Plugin System
│   ├── Plugins (static registry + loader)                 OpenRPA.Interfaces/Plugins.cs
│   ├── IPlugin / IRecordPlugin / IDetectorPlugin / IRunPlugin / IStorage / ISnippet /
│   │   ICustomWorkflowExtension                           OpenRPA.Interfaces/I*.cs
│   └── NuGet project dependencies → Documents/OpenRPA/extensions
│                                                          OpenRPA/NuGet/NuGetPackageManager.cs
│
├── Automation Providers (all implement IRecordPlugin + own Selector + IElement)
│   ├── Windows   OpenRPA.Windows   (FlaUI UIA3)  → WindowsSelector / UIElement
│   ├── Browser   OpenRPA.NM        (native messaging) → NMSelector / NMElement
│   │             OpenRPA.NativeMessagingHost (exe)  + extension JS
│   ├── Office    OpenRPA.Office    (COM Interop: Excel/Word/Outlook/PowerPoint)
│   └── Other     OpenRPA.IE, .Java(+JavaBridge), .SAP(+SAPBridge), .Image (Emgu.CV),
│                 .Script (C#/VB/PowerShell/Python/AHK), .TerminalEmulator, .Forms, .Database, ...
│
├── Recorder
│   ├── InputDriver (WH_MOUSE_LL / WH_KEYBOARD_LL hooks)   OpenRPA.Interfaces/Input/InputDriver.cs
│   ├── IRecordPlugin.Start/OnUserAction/ParseUserAction
│   └── MainWindow.OnUserAction → WFDesigner.AddRecordingActivity
│
├── Selector System
│   ├── Selector / SelectorItem / SelectorItemProperty (JSON array) OpenRPA.Interfaces/Selector/
│   ├── WindowsSelector / WindowsSelectorItem              OpenRPA.Windows/
│   └── NMSelector / NMSelectorItem                        OpenRPA.NM/
│
├── Events / Detectors
│   ├── IDetectorPlugin (+ Detector entity)                OpenRPA/Detector.cs
│   ├── Implementations: WindowsClickDetector, WindowsElementDetector, KeyboardDetector,
│   │   URLDetector, DownloadDetector, FileWatcherDetector, MSSpeech, JavaClickDetector
│   └── Detector activity (bookmark "detector_<id>")       OpenRPA/Activities/Detector.cs
│
├── Native Messaging
│   ├── Extension (MV2) bootstrap → evals scripts from host  OpenRPA.NativeMessagingHost/addon*/
│   ├── OpenRPA.NativeMessagingHost.exe (stdio ↔ named pipe)
│   └── NamedPipeWrapper (JSON length-prefixed frames)     OpenRPA.NamedPipeWrapper/
│
├── Orchestration (client side of OpenFlow)
│   ├── WebSocketClient (JSON command protocol)            OpenRPA.Net/WebSocketClient.cs
│   ├── Robot queue: RegisterQueue(user._id) + role queues RobotInstance.RegisterQueues
│   ├── RobotCommand {command: invoke|killworkflow|...}   OpenRPA.Interfaces/mq/RobotCommand.cs
│   ├── Work items (Add/Pop/Update/Delete/BulkAdd)         OpenRPA/Activities/Workitems/
│   └── RDService (Windows service, auto RDP logon)        OpenRPA.RDService/
│
└── Storage / Configuration / Logging / Telemetry
    ├── StorageProvider → Plugins.Storages (LiteDB, Filesystem)  OpenRPA/StorageProvider.cs
    ├── LocallyCached (local + OpenFlow sync)               OpenRPA/LocallyCached.cs
    ├── Config (settings.json + HKLM/HKCU\SOFTWARE\OpenRPA, DPAPI) OpenRPA.Interfaces/Config.cs
    ├── Log (static; Trace + NLog file)                     OpenRPA.Interfaces/Log.cs
    └── Tracing (TraceListener) + OpenTelemetry OTLP        OpenRPA/Tracing.cs, RobotInstance.InitializeOTEL
```

## End-to-end trace (PRD §0.5 Definition of Done)

```text
User clicks Run (toolbar)
  → MainWindow.PlayCommand → MainWindow.OnPlay()                  OpenRPA/MainWindow.xaml.cs:888, 2650
  → WFDesigner.Run(VisualTracking, SlowMotion, null)              OpenRPA/Views/WFDesigner.xaml.cs:1410
  → Workflow.CreateInstance(params, queue, corrId, IdleOrComplete, OnVisualTracking, ident)
                                                                  OpenRPA/Workflow.cs:569
  → WorkflowInstance.Create(...)                                  OpenRPA/WorkflowInstance.cs:171
      → IRunPlugin.onWorkflowStarting(...) veto hook
      → Workflow.Activity()  (ActivityXamlServices.Load, CompileExpressions=true)  Workflow.cs:535
      → createApp(): new System.Activities.WorkflowApplication(activity, Parameters)
                     + WorkflowTrackingParticipant + ICustomWorkflowExtension instances
                     + OpenFlowInstanceStore (if Serializable)    WorkflowInstance.cs:226
  → WorkflowInstance.Run() → wfApp.Run()                          WorkflowInstance.cs:730
  → WF4 scheduler executes activities, e.g. Windows GetElement (BreakableLoop/NativeActivity)
      → WindowsSelector.GetElementsWithuiSelector(sel, from, max, ext)   OpenRPA.Windows/WindowsSelector.cs:501
          → FlaUI UIA3Automation tree walk + WindowsSelectorItem.Match(...)
      → context.ScheduleAction<UIElement>(Body, element)          OpenRPA.Windows/Activities/GetElement.cs
  → ClickElement.Execute: Element.Get(context).Click(virtual, button, x, y, dbl, animate)
                                                                  OpenRPA/Activities/ClickElement.cs
      → UIElement.Click → UIA InvokePattern.Invoke() or InputDriver mouse click
                                                                  OpenRPA.Interfaces/UIElement.cs:307
  → wfApp.Completed / Aborted / OnUnhandledException delegates     WorkflowInstance.cs:799
      → state, errormessage, Parameters[outputs], IRunPlugin.onWorkflowCompleted/Aborted
      → OnIdleOrComplete → WFDesigner.IdleOrComplete (reply "invoke<state>" to OpenFlow if remote)
```

## Key characteristics (Observed)

- One process hosts UI, designer, runtime, recorder, plugin host, IPC server, and OpenFlow client.
- Heavy use of **static/global mutable state**: `RobotInstance.instance`, `global.webSocketClient`,
  `Plugins.*` static collections, `WorkflowInstance.Instances`, `Config.local`, `NMHook` statics.
- Core contracts assembly `OpenRPA.Interfaces` also contains WPF views, FlaUI-based `UIElement`,
  input hooks, NLog logging, named-pipe dependency, registry access, and OpenFlow entity types.
- No automated test projects exist in the solution (no `*Test*.csproj` found).

## Not yet confirmed

- Internal behavior of the IE, Java, SAP, Image, TerminalEmulator, and Forms plugins beyond their
  plugin-interface registration (not read in depth; out of Phase 0 priority).
- OpenFlow server-side semantics (queues, workitem retry scheduling) — server is not in this repo;
  only the client protocol was traced.
