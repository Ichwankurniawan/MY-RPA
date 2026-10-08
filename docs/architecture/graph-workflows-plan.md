# Graph workflows — implementation plan (proposed)

Status: **approved** (owner, 2026-10-08: "merge PR#7 to main,proceed G1"). G-1, G-2 and G-3 done (2026-10-08). Phase 6 still needs the owner's authorization. Implements [ADR-0037](../adr/0037-flowchart-and-state-machine-workflows.md)
(accepted): format 1.1, SDK 1.1, `Core.Flowchart` / `Core.Decision` first, then `Core.StateMachine` / `Core.State`.
Follows the Studio UX slice (UX-1 to UX-3). Phase 6 is not part of this plan and still needs its own authorization.

## Rules (unchanged)
- The v1.0 JSON family stays the only workflow format; 1.1 is an additive minor version (format §8). Files without
  graphs keep `"schemaVersion": "1.0"` and are byte-for-byte unchanged.
- One workflow model for CLI, Server and Studio; graph semantics live in the engine only (`ExecuteStepAsync`), never in
  the Studio or in each activity.
- No new packages: no diagram library (ADR-0037 option E), no DnD library (ADR-0028); SVG attributes only (CSP).
- Existing events only (`node.started` / `node.completed`); no new event kinds.

## Slices

### G-1 — Engine, format 1.1, SDK 1.1, flowchart activities (no Studio change)

| Area | Change |
|---|---|
| `MyRPA.Workflow` model | `WorkflowSchemaVersion.Current` = 1.1. `NodeDefinition` gains `Transitions` (`TransitionDefinition`: `To` node id, optional `When` expression, optional `Label`) and `Layout` (`NodeLayout`: `X`, `Y`, finite numbers). Both optional; constructors enforce invariants only |
| Reader / writer | `WorkflowStructureReader` reads `transitions` and `layout` (structural errors at their JSON paths); `WorkflowJsonWriter` writes them, and writes `schemaVersion` as loaded (a 1.0 file stays 1.0) |
| `MyRPA.Core` | `ActivityChildLayout { List, Graph }`; `ActivityDescriptor.ChildLayout` (optional constructor parameter, default `List`; `Graph` requires `allowsChildren`) |
| Catalog | `childLayout` written to the snapshot; catalog version 1.1, readers accept 1.0 (missing = `List`) (ADR-0020) |
| SDK 1.1 | `AutomationSdk.Version` = 1.1. `IActivityContext.ExecuteStepAsync(NodeDefinition step)` → `ValueTask<NodeDefinition?>`: refuses a node that is not a direct child of a graph container, runs it like `ExecuteAsync`, then evaluates its `when` expressions in order in the container's scope and returns the first matching sibling (or null). Plugins built for 1.0 still load (the version rule is unchanged) |
| Validation | MYRPA1053–1057 as in the ADR. **One addition: MYRPA1058** "uses `transitions`, `layout` or a graph container, which need schema 1.1", so a 1.0 file cannot silently depend on 1.1 features (refinement recorded in ADR-0037). `when` expressions reuse MYRPA1043/1044 at `$...transitions[i].when`. The unreachable-step check (1056) is a warning |
| Activities | `Core.Flowchart` (`maxSteps`, default 10,000; Graph children): starts at the first child, loops `ExecuteStepAsync` until null, checks cancellation between steps, MYRPA2010 when `maxSteps` is exceeded. `Core.Decision`: no behavior (its transitions carry the conditions). Category "Control Flow"; two new icons in G-2 |
| Errors | `WorkflowErrorCodes`: MYRPA2010 (max steps). MYRPA2011 is reserved for G-3 |
| Samples | `samples/flowchart.json` (a retry loop: try → decide → retry or done), valid (`ShippedSamples_AreValid`), runnable from the CLI |
| Corpus | New corpus files for 1053–1058 with their locations. `tests/corpus/expected/locations.json` was produced by the archived WPF parity test, so it is frozen: new entries are hand-written and checked by the Studio's `corpus.test.ts`; the .NET side asserts each file's codes and paths |

Tests: model and reader/writer round-trip (with and without graphs, 1.0 files unchanged), catalog 1.0/1.1, loader codes
1053–1058 and expression paths, runner (start step, first matching transition, no transition ends, loops back, max steps,
cancellation mid-graph, failure in a step propagates and is attributed to the step, events per step including repeats,
nested flowchart in a Sequence and vice versa), `ExecuteStepAsync` refusals, a plugin graph container through the
plugin host (SDK 1.1), CLI `validate`/`run` of the sample, architecture tests (no new references).

Docs: `workflow-format.md` (1.1: transitions, layout, graph containers, codes, compatibility), `execution-model.md`
(graph execution, events per step), `automation-sdk.md` (SDK 1.1), ADR-0037 refinement note (1058), ADR-0013/0020
amendment notes.

### G-2 — Studio flowchart canvas and keyboard view

| Area | Change |
|---|---|
| Document model | `document.ts` reads and keeps `transitions` / `layout`; indexes graph containers; diagnostics located at `transitions[i].when`. 1.1 files open editable (round-trip unchanged); a 1.0 file is raised to 1.1 in the same undoable step that adds its first graph container (and never otherwise) |
| Edits (pure, with refusal reasons) | add / edit / reorder / delete a transition; set `when` and `label`; move a step (writes `layout`, rounded to whole pixels); delete a step also removes the transitions pointing to it (one undo step, reported in the status); cut/paste of steps renames ids and rewrites `to` targets inside the pasted set, drops targets outside it; inserting into a graph appends a step (the first child is the start step; "Set as start" moves it first) |
| Canvas | Opening a `Core.Flowchart` shows a canvas in place of its child list: step cards at `layout` (missing positions auto-placed by a simple layered layout from the start step, not saved until moved), SVG arrows with labels and a start marker, drag a card to move it, drag from a card's handle to another card to add a transition, select an arrow to edit `when` / `label` in Properties. Pointer hit-testing and the overlay as in UX-3; zoom reuses the designer zoom |
| Keyboard view | An equivalent list view (toggle "Canvas / List"): steps in order, each with its transitions in a table (add, edit, reorder, delete). The axe check and the keyboard pass cover both |
| Steps | A step that is a container (for example a Sequence) opens in the card designer with breadcrumbs (back to the canvas) |
| Runs | Node badges on step cards as today; the arrow last taken is highlighted from consecutive steps of the same container (ADR-0037 §5); repeated steps show their latest state |

Tests: Vitest for every edit and refusal, the schema raise and its undo, the canvas (render, move, connect, select an
arrow) and the list view; smoke (build `flowchart.json` from scratch by pointer and by keyboard, validate, save, run,
the taken arrows); manual and a11y extended with canvas and list states; perf: a 200-step flowchart (open, move a card,
draw an arrow, typing in Properties: p95 ≤ 50 ms) next to the existing 3,000-node targets.

### G-3 — State machine
- Engine: `Core.StateMachine` (Graph children, only `Core.State`) and `Core.State` (`final`; slots `entry`, `exit`);
  `entry` → transitions (conditions only, owner decision 3) → `exit` → next state; MYRPA2011 for a non-final state
  with no transition taken; 1055/1057 checks for states. Sample `samples/state-machine.json` (init → get work →
  process → end).
- Studio: the same canvas and list view; states show their `entry` / `exit` slots, final states are marked; G-2 edits
  apply unchanged.
- Docs and tests as in G-1 and G-2.

### G-1 result (2026-10-08)
- Delivered as planned: format 1.1 (`transitions`, `layout`), `ActivityChildLayout` / `ActivityDescriptor.ChildLayout`,
  catalog 1.1, SDK 1.1 (`ExecuteStepAsync`), MYRPA1053–1058, MYRPA2010, `Core.Flowchart`, `Core.Decision`,
  `samples/flowchart.json` (CLI: Succeeded after 3 attempts), corpus files and their CLI check. Details in ADR-0037's
  implementation notes.
- One Studio change was needed already: diagnostics at `…transitions[i]…` and `…layout…` are shown on the node that
  owns them (`workflowData.ts`), otherwise a 1.1 file's problems appeared on the parent. Everything else in the Studio
  is G-2; until then a 1.1 file opens and round-trips unchanged (transitions and layout are kept as data).
- Validation: .NET 886/886 (Debug and Release), Studio Vitest 231/231, typecheck, smoke, manual (steps 1–11), accessibility (0 serious or critical), perf (all targets), `dotnet format` clean, Release build 0 warnings.
- The Studio does not offer graph containers in the activity panel until G-2 (catalog `childLayout: Graph`).

### G-2 result (2026-10-08)
- Delivered as planned: the canvas (positions, auto-placement, arrows with labels, move, connect, click an arrow),
  the Transitions editor in Properties (the keyboard way), a list view per flowchart, opening a container step with
  breadcrumbs and a way back, the schema raise in the same undo step, step deletion and moves that remove dangling
  transitions, paste that keeps transitions among the pasted steps, drops from the activity panel at the drop point,
  and the arrow taken last during a run. G-1's "graph containers are not offered" filter is gone.
- **Changed from the plan:** "Set as start step" lives in the Transitions section (Properties), not on the canvas card;
  the keyboard view is the flowchart's List view (the existing cards, with each step's transitions), plus the
  Transitions editor, rather than a separate table. Canvas steps are tree items, so the designer's keyboard navigation
  and focus handling work unchanged.
- **Bug found by the browser test (W7, latent):** a drag released where the browser sends no click (over another
  element) left the "ignore the click after a drag" flag set, so the next real click was swallowed. Both the designer's
  drag-and-drop and the canvas now clear it on every new press. The canvas also captures the pointer only once a press
  becomes a drag (capture retargets the click away from the card).
- Validation: Vitest 252/252, typecheck, smoke (new G-2 step in Chromium), manual (steps 1–11), accessibility (0
  violations in all 12 states, including canvas, dark canvas and list view; keyboard passes), perf in two consecutive
  runs: every 3,000-node target met; 200-step flowchart open 91–112 ms, typing p95 18 ms, card move p95 18–19 ms,
  arrow drawing p95 19–22 ms (targets 1,000 / 50 / 50 / 50 ms). No .NET change.

### G-3 result (2026-10-08)
- Delivered: `Core.StateMachine` and `Core.State` (entry → transitions → exit; conditions only), MYRPA1055 for a step
  that is not a state, MYRPA1057 for a final state with transitions, MYRPA2011 at run time,
  `samples/state-machine.json` (CLI: "Processed 3 of 3 items"), `tests/corpus/diag-state.json`, two icons and a final
  badge in the Studio.
- **One SDK addition** beyond G-1: `IActivityContext.ChooseTransition()`, so a state decides between `entry` and
  `exit` as ADR-0037 §2 says (details in the ADR's G-3 notes).
- Validation: .NET 898/898 (Release; `dotnet format` clean, 0 warnings), Vitest 255/255, smoke (new G-3 step: the
  sample on the canvas, run, final state and the arrow taken), manual (steps 1–11), accessibility (0 violations in all
  13 states), perf (every target met).

## Order and delivery
G-1 → G-2 → G-3, each on its own branch with its own PR, report and CI. G-2 starts from `main` after PR #7 (UX-3) is
merged, because the canvas reuses the UX-3 designer (zoom, overlay, cards). G-1 touches only .NET code and can start
first.

## Out of scope
Event or timer triggers for state transitions (a later decision), a minimap, automatic scrolling while dragging,
breakpoints or a debugger, anything from Phase 6.
