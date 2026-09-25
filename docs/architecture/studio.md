# MyRPA Studio

Status: Phase 5 (2026-09-25). Decisions: [ADR-0018](../adr/0018-studio-architecture.md) (architecture),
[ADR-0019](../adr/0019-plugin-configuration-file.md) (plugin configuration), [ADR-0020](../adr/0020-activity-catalog-snapshot.md)
(catalog snapshot).

## 1. Start

```bash
dotnet run --project src/MyRPA.Studio
dotnet run --project src/MyRPA.Studio -- samples/control-flow.json
dotnet run --project src/MyRPA.Studio -- --plugin samples/plugins/MyRPA.Samples.DemoPlugin/bin/Debug/net10.0 samples/plugins/demo-plugin.json
```

Command line: `MyRPA.Studio [workflow.json] [--plugin <dir>]... [--plugin-config <file>]`. Plugins load exactly as in
the CLI: they are required, validated before any of their code runs, and fully trusted (ADR-0015). If a plugin fails
to load, Studio shows the diagnostics and exits with code 5. Studio is a Windows application (`net10.0-windows`, WPF).

## 2. Layout (PRD 5.2)

```text
File Edit View Debug                                         [Run] [Stop]
┌ Activities ─┬──────── Workflow designer ────────┬── Properties ──┐
│ search      │ Sequence                          │ workflow, or   │
│ Control Flow│  ├ drop zone                      │ selected block │
│ Data        │  ├ If ─ then: [block] else: [zone]│ id, name,      │
│ Workflow    │  └ drop zone                      │ one editor per │
│ Diagnostics │                                   │ property       │
│ <plugins>   │                                   │                │
├─────────────┴───────────────────────────────────┴────────────────┤
│ Variables | Arguments | Output | Logs | Errors (n)               │
└──────────────────────────────────────────────────────────────────┘
```

The toolbox is built from `IActivityCatalog`, so plugin activities (for example `Browser.*`) appear under their own
categories without any Studio change.

## 3. Projects

| Project | Contents |
|---|---|
| `MyRPA.Studio.Core` (`net10.0`) | `Documents/`: `WorkflowDraft`, `NodePath`, `DraftJson`. `Editing/`: `DraftEdits`, `DocumentHistory`, `DraftClipboard`, `DraftValidator`. `ViewModels/`: `StudioViewModel`, `NodeViewModel`/`SlotViewModel`/`DropZoneViewModel`, `ToolboxViewModel`, `PropertiesViewModel`/`PropertyEditorViewModel`, `VariablesViewModel`, `ArgumentsViewModel`, `OutputViewModel`, `LogViewModel`. `Running/`: `RunMonitor`, `StudioLogFeed`. `Services/`: `IStudioDialogs`, `IStudioClipboard`, `IUiDispatcher`, `IWorkflowStorage` |
| `MyRPA.Studio` (`net10.0-windows`) | `App` (async startup/shutdown), `StudioComposition` (host, command line), `MainWindow.xaml` + `Designer/StudioTheme.xaml` (templates), `DesignerBehaviors` (drag, drop, select), `Commit` (commit editors on Enter or when leaving the field), WPF dialogs, clipboard and dispatcher |

## 4. Document model and editing

- The document is a `WorkflowDraft`: an immutable record tree shaped like the JSON file.
  - It keeps incomplete values (for example an empty required property) and unknown JSON fields.
  - Expression text and JSON literals (`500`, `true`) are kept exactly as written.
- A block is addressed by a `NodePath`, for example `/children[1]/slots[then]`.
- Every change is one `DraftEdits` function. Available edits:
  - Insert, Move, Remove;
  - SetProperty, SetId, SetDisplayName, SetInfo;
  - arguments and variables.
  - A refused edit (for example a slot that is already occupied) raises `EditException`. Studio shows it as a message
    and the document is unchanged.
- Undo/redo is snapshot based (200 steps). The Edit menu shows what will be undone.
- The clipboard holds `{"myrpaNodes":"1.0","nodes":[…]}` text. Pasting renames ids that already exist.
- Paste and toolbox double-click place the activity:
  - after the selected block, when the selection is in a list;
  - otherwise into the selected container.

## 5. Validation

After every change the draft is written to JSON and validated by the engine's `WorkflowLoader`, so Studio and the CLI
cannot disagree about validity. Each diagnostic's JSON path is mapped back to where it belongs:
- a block (red border, message on the block);
- a property editor;
- an argument or variable row;
- the workflow.

All diagnostics are listed under Errors; double-click one to select its block. While you type an expression, a syntax
hint appears before you commit it.

## 6. Running

Run (F5):
1. Validates the workflow. An invalid workflow is not started.
2. Asks for In/InOut arguments. Values are text; a blank value uses the default.
3. Runs the workflow with `IWorkflowRunner`.

While it runs:
- A `RunMonitor` follows the engine's tracing spans for the run's correlation id. Each block shows *running*, ✓ or
  *failed*, and the failed block is highlighted.
- Output shows the status, duration, execution id, outputs and the error.
- Logs shows `Core.Log` and plugin messages together with their node id.

Stop (Shift+F5) cancels the run cooperatively.

## 7. Tests

- `MyRPA.Studio.Core.Tests` (headless, 80 tests) covers:
  - the draft JSON round trip;
  - edits and undo/redo;
  - diagnostic mapping;
  - every view model;
  - runs through the real engine: success, failure highlight, cancel, timeout (fake clock), bad arguments.
- `MyRPA.Studio.Tests` (Windows only) covers:
  - building the real window, driving it and rendering it to PNG files in `bin/…/screenshots/`;
  - closing with unsaved changes;
  - composition and the command line;
  - the architecture code rules on the WPF assembly.

## 8. Manual test script (PRD 5.5)

1. Start Studio. The title is *Untitled — MyRPA Studio*, and the Errors tab shows *(0)*.
2. **Create:** in Properties set Name to `Greeter`, then press Enter. The title gains `*`.
3. **Add activities:** drag *Assign* from Activities onto the *Drop activities here* zone. Then drag *If* below it,
   and *Log* into the If's `then` slot. Blocks with missing required properties turn red, and Errors lists them.
4. **Configure:**
   - Arguments tab: add an argument, name it `who`, and set the default to `"World"`.
   - Variables tab: add `message` (String).
   - Select Assign: set `to` = `message` (pick it from the list) and `value` = `'Hello, ' + who`.
   - Select If: set `condition` = `len(who) > 0`.
   - Select Log: set `message` = `message`.
   - Errors shows *(0)*.
5. **Undo/redo:** press Ctrl+Z (the Log message is removed), then Ctrl+Y (it returns).
6. **Copy/paste/delete:** select Assign, press Ctrl+C, then Ctrl+V. A copy with a new id (for example `assign-2`)
   appears after it. Delete it with Del. (A slot holds one block, so pasting needs a list or a container selected.)
7. **Save:** press Ctrl+S and save as `greeter.json`. The `*` disappears. `myrpa validate greeter.json` reports it valid.
8. **Open:** File › New, then File › Open `greeter.json`. The workflow is back.
9. **Run:** press F5 and enter `Ada`. Output shows *Succeeded*, Logs shows *Hello, Ada* for node `log…`, and every
   block shows ✓.
10. **Failure:** add a *Throw* with `message` = `'boom'` at the end and run. Output shows *Failed*, and the Throw block
    is red with *failed*.
11. **Close:** close the window. Studio asks to save the unsaved change.
