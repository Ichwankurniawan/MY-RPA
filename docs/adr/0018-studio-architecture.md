# ADR-0018: Studio architecture (platform-neutral Studio.Core, WPF shell)

- Status: Accepted
- Date: 2026-09-25
- Phase: 5
- Amends: ADR-0003 (two new projects), ADR-0004 (one project may use WPF)
- Builds on: ADR-0005 (composition), ADR-0010 (execution model), ADR-0011 (workflow format)

## Context
PRD Phase 5 asks for a WPF workflow designer with a fixed layout (PRD 5.2) and a list of requirements (PRD 5.3):
- drag, drop and nesting of activities;
- properties, variables and arguments;
- save, open, undo, redo, delete, copy/paste;
- run.

PRD 5.4 adds that the designer must consume the same workflow model as the CLI and runtime, with one execution engine.

The ADR-0004 rules forbid WPF in every `src` project. A web Studio may follow later. Studio logic therefore must not
depend on WPF.

## Decision

### Two projects
- **`MyRPA.Studio.Core`** holds every Studio decision:
  - Contents: the document model, edits, undo/redo, the clipboard format, validation mapping, view models, run monitoring and the UI service interfaces.
  - Dependencies: plain `net10.0`, referencing Core and Workflow only.
  - Packages: `CommunityToolkit.Mvvm` and `Microsoft.Extensions.Logging.Abstractions`.
  - Testing: tested headless with the real engine.
- **`MyRPA.Studio`** is the WPF shell and a composition root:
  - Target: `net10.0-windows`, `UseWPF`, `WinExe`.
  - Contents: windows, XAML templates, drag-and-drop behaviors, and dialog/clipboard/dispatcher implementations.
  - Composition: the same host as the CLI (runtime, activities, storage, plugin host).
  - Constraint: it is the only project that may use WPF.

### Architecture rules
- `ProjectRule.IsDesktopUi` marks the WPF project. `DesktopUi_IsOnlyInTheStudioShell` checks that only `MyRPA.Studio` has it.
- `MyRPA.Architecture.Tests` targets `net10.0` and cannot load the WPF assembly. The code rules for it therefore run in
  `MyRPA.Studio.Tests`, with the same detectors (`IlScanner.cs` and `CodeRuleDetectors.cs`, linked):
  - no `async void`;
  - no mutable statics;
  - no banned APIs;
  - an allow-list of compiled references.
- Composition roots may be `Exe` or `WinExe`.

### Document model
- **Draft model:**
  - The designer edits a `WorkflowDraft`, an immutable, JSON-shaped record tree.
  - It can hold incomplete or invalid workflows, for example while a required property is still empty.
  - It preserves unknown JSON fields.
  - It is not a second workflow model. It is an editing buffer that is always converted by `DraftJson` to the v1.0 file format.
- **Validation:** runs after every change by writing the draft and passing it through the engine's `WorkflowLoader`.
  Diagnostics are mapped back to blocks, properties, arguments and variables through the JSON paths recorded while writing.
- **Runs:** use the same `IWorkflowRunner` with the loader's `WorkflowDefinition`.
- **Edits and history:**
  - Edits are pure functions (`DraftEdits`) that throw `EditException` when refused.
  - Undo/redo stores snapshots (`DocumentHistory`, 200 steps).
  - Dirty tracking compares against the saved snapshot by reference.

### Designer
- The designer uses structured nested blocks, not a free-form canvas:
  - Lists of children have drop zones between the items.
  - Slots show their block or an empty drop zone.
  - Prefix slots (`case:`) ask for a name when something is dropped on them.
- Everything is generated from `ActivityDescriptor`. Plugin activities therefore appear in the toolbox and designer
  without any Studio change.
- Property editors follow the property kind:
  - expression: live syntax feedback;
  - text: a list when allowed values exist;
  - assignment target: suggested variable names;
  - local name;
  - maps: a key/value list.
- Copy/paste uses a text clipboard format, `{"myrpaNodes":"1.0","nodes":[…]}`. Pasted ids are made unique.

### Running
- The running-node highlight uses an `ActivityListener` on the engine's `MyRPA.Runtime` spans, filtered by the run's
  correlation id. No engine change is needed.
- Logs reach the Logs pane through a logging provider, `StudioLogFeed`. The node id comes from the engine's logging scope.
- Input arguments are asked for before a run and parsed with `WorkflowValues.TryParseText`. A blank answer uses the default.

### Threading
- The UI never blocks:
  - Commands are async (`AsyncRelayCommand`).
  - Results are posted through `IUiDispatcher`.
  - Startup (plugins, host) and shutdown are asynchronous.
- There is no `async void` and no `.Wait()`. Closing cancels the first `Closing` event, asks about unsaved changes, and
  closes afterwards.

## Consequences
- A future web Studio can reuse `MyRPA.Studio.Core` and implement the three UI service interfaces.
- The designer is not a flowchart editor. Free-form layout, breakpoints and multiple open documents are out of scope
  for Phase 5.
- The view models validate on every change. Workflows of normal size validate in milliseconds; very large workflows
  may need incremental validation later.
- Linux CI can compile the shell (`EnableWindowsTargeting`) but runs its tests only on Windows.
