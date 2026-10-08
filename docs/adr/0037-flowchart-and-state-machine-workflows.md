# ADR-0037: Flowchart and state-machine workflows (graph containers)

- Status: **Accepted** (2026-10-08, owner: "proceed your suggestion, I agree with them"). Implementation follows the
  Studio UX slice and comes before Phase 6.
- Phase: post-Phase 5 slices (graph workflows), authorized by the owner on 2026-10-08.
- Would amend: [ADR-0011](0011-workflow-json-format-and-validation-pipeline.md) (format 1.1),
  [ADR-0013](0013-automation-sdk-and-activity-contract.md) (SDK 1.1, additive), [ADR-0020](0020-activity-catalog-snapshot.md)
  (catalog field), and the scope note of [ADR-0018](0018-studio-architecture.md) ("the designer is not a flowchart
  editor"). [ADR-0023](0023-first-class-execution-events.md) needs no change.

## Context
Workflows today are trees: `Core.Sequence` runs `children` in order, other activities run named `slots`. Loops and
branches exist (`While`, `If`, `Switch`, `TryCatch`), but there is no way to express a **graph**: steps connected by
arrows, with loops back to earlier steps (a flowchart), or named states with conditional transitions (a state
machine, the usual shape of a long-running "init → get work → process → end" robot).

Three facts about the current model shape every option:
1. An activity may execute only its **own** children and slots (`IActivityContext.ExecuteAsync`), and it can evaluate
   only its **own** properties (`Evaluate`, `EvaluateMap`). ADR-0013 froze this contract for SDK 1.0.
2. `ExecuteAsync` returns nothing and `ActivityResult.Completed` is the only outcome, so a child cannot tell its
   container "continue with step X".
3. The JSON has no designer data (positions). Unknown fields are warnings (MYRPA1005); a minor format version may add
   optional fields and activities (format §8).

## Options considered

| Option | Summary | Verdict |
|---|---|---|
| A. Encode the graph in properties (e.g. an `ExpressionMap` of `"from>to": condition`) | No format change | Rejected: map keys cannot hold node ids (`-`, `.`, `:`), order is not a contract, nothing validates the targets |
| B. A run-scoped "flow control" service that steps write their next target into | No format or SDK change | Rejected: hidden coupling between activities, needs keying for nested graphs, invisible to validation and the Studio |
| C. A separate graph file format (like XAML flowcharts) | Rich designer data | Rejected: v1.0 JSON is the only workflow format (ADR-0021, Phase 5 rules) |
| D. **Transitions as structure** (format 1.1) **plus one SDK addition** that runs a step and resolves its transition | Validated, visible, engine-owned | **Proposed** |
| E. A third-party diagram library for the canvas (e.g. React Flow) | Fast to build | Rejected for the Studio: inline styles break the CSP (`default-src 'self'`), a large runtime dependency, and the DnD/UI-library rule (ADR-0028) |

## Proposed decision (option D)

### 1. Format 1.1 (additive; files without graphs stay 1.0)
- A descriptor may declare **`childLayout: "Graph"`** (default `"List"`). Its `children` are the graph's steps; the
  **first child is the start step**.
- Every direct child of a graph container may have **`transitions`**: an ordered array of
  `{ "to": "<sibling id>", "when": "<expression>"?, "label": "<text>"? }`. After the step completes, the first
  transition whose `when` is true (or that has no `when`) is taken. No transition taken → the container completes.
- Any node may have **`layout`**: `{ "x": number, "y": number }`. Designer-only; the engine ignores it.
- A file that uses `transitions`, `layout` or a graph container declares `"schemaVersion": "1.1"`. A 1.0 reader rejects
  it with MYRPA1011 (the existing policy). The Studio raises the version when the first graph container is added.

```json
{ "id": "flow", "type": "Core.Flowchart", "properties": { "maxSteps": 10000 },
  "children": [
    { "id": "init", "type": "Core.InvokeWorkflow", "properties": { "workflow": "init.json" },
      "layout": { "x": 80, "y": 60 },
      "transitions": [ { "to": "get-item", "label": "ok" } ] },
    { "id": "get-item", "type": "Core.Assign", "properties": { "to": "item", "value": "pending[0]" },
      "transitions": [ { "to": "process", "when": "item != null" }, { "to": "done" } ] },
    { "id": "process", "type": "Core.Sequence", "children": [ ], "transitions": [ { "to": "get-item" } ] },
    { "id": "done", "type": "Core.Log", "properties": { "message": "'finished'" } }
  ] }
```

### 2. Built-in activities (`Core`, category "Control Flow")

| Type | Properties | Children / slots | Behavior |
|---|---|---|---|
| `Core.Flowchart` | `maxSteps` (expr → Int, default 10,000) | `children` (Graph) | Runs steps from the first, following transitions; exceeding `maxSteps` fails the node (MYRPA2010) |
| `Core.Decision` | — | — | Does nothing; exists so the designer can draw a decision (its transitions carry the conditions) |
| `Core.StateMachine` | `maxSteps` | `children` (Graph): `Core.State` only | Like a flowchart over states; ends at a final state |
| `Core.State` | `final` (expr → Boolean, default false) | slots `entry`, `exit` | Runs `entry`, then its transitions decide the next state, then `exit` runs before leaving. A non-final state with no transition taken fails (MYRPA2011, "no transition from state X"); a final state must not have transitions |

State-machine transitions are **conditions only** in this ADR (evaluated after `entry`). Waiting for external triggers
(events, timers) is a later decision.

### 3. SDK 1.1 (additive, ADR-0013 versioning)
- `ActivityDescriptor.ChildLayout` (`List` | `Graph`), written to the catalog snapshot as `childLayout` (catalog
  minor version; ADR-0020).
- `IActivityContext.ExecuteStepAsync(NodeDefinition step)` → `ValueTask<NodeDefinition?>`: runs a direct child of a
  graph container, then evaluates that child's transitions in order and returns the target sibling (or null). It is
  the only way to evaluate transition expressions, so graph semantics live in the engine, not in each activity.
  Plugins may build their own graph containers on it.
- Everything else in the SDK is unchanged; `ActivityResult.Completed` stays the only outcome.

### 4. Validation (`WorkflowLoader`, new codes)

| Code | Problem |
|---|---|
| MYRPA1053 | Transition target is not a sibling in the same graph container |
| MYRPA1054 | `transitions` on a node whose parent is not a graph container |
| MYRPA1055 | Graph container has no steps / a `Core.StateMachine` child is not a `Core.State` |
| MYRPA1056 | Step unreachable from the start step (warning) |
| MYRPA1057 | Final state with transitions / invalid `layout` |

`when` expressions reuse MYRPA1043/1044 at `$...transitions[i].when`. Runtime: MYRPA2010 (max steps), MYRPA2011 (no
transition from a non-final state).

### 5. Execution events and the Studio
- No new event kinds: every step emits `node.started` / `node.completed` as today, so run views and logs work as they
  are. The Studio infers the edge taken from consecutive steps of the same container.
- The Studio shows a graph container as a **canvas**: steps at `layout` positions (auto-placed when missing), SVG
  arrows, drag to move (writes `layout`), drag from a step's edge handle to another step to add a transition, select
  an arrow to edit `when` / `label`. Plain pointer hit-testing as in W7; SVG attributes only (CSP-safe).
- **Accessibility:** the canvas has an equivalent keyboard view: steps as a list, each with its transitions in a
  table (add, edit, reorder, delete). The axe check and the keyboard pass cover both.
- Opening a step (for example a State's `entry` Sequence) shows it in the existing tree designer, with breadcrumbs.

## Consequences
- The workflow model gains its first non-tree structure; the engine owns graph semantics in one place.
- Corpus, round-trip and diagnostic-location tests (ADR-0035) extend to `transitions` paths
  (`$.root.children[2].transitions[0].when`).
- Files that use graphs need a 1.1 reader: older CLIs and robots reject them clearly.
- Work estimate: engine + format + SDK + validation (one slice), Studio canvas and keyboard view (one or two slices),
  samples and docs.

## Implementation notes (G-1, 2026-10-08)
Refinements made while implementing the engine slice ([plan](../architecture/graph-workflows-plan.md)):
- **MYRPA1058** (added): a file that uses `transitions`, `layout` or a graph container but declares a schema version
  before 1.1 is an error, so a 1.0 file cannot silently depend on 1.1 features.
- **MYRPA1057** covers a `layout` coordinate that is not a finite number; a missing or non-number coordinate is
  MYRPA1003 / MYRPA1004 like any other field. Final states (G-3) reuse 1057.
- `ExecuteStepAsync`'s parameter is named `node` (as in `ExecuteAsync`; `step` is a reserved word in Visual Basic,
  CA1716). The engine refuses a node that is not a child; that only graph containers' steps have transitions is the
  loader's guarantee (MYRPA1054), so the runner needs no catalog.
- A `when` that is not Boolean fails the container with MYRPA2003; MYRPA2010 carries `errorType` `Graph`.
- The writer keeps a file's schema version; `WorkflowDefinition` defaults to 1.1 only when a node has transitions or a
  layout. `GET /api/info` lists `["1.0", "1.1"]`; the catalog snapshot is version 1.1.
- Corpus: `tests/corpus/diag-graph*.json`. Their expected placements are hand-written (the WPF Studio is archived),
  marked with `source`, and `CorpusDiagnosticsTests` checks them against the real CLI.

## Implementation notes (G-2, 2026-10-08)
- The keyboard-equivalent view (§5) is a flowchart's **List view** (its steps as designer cards with their
  transitions) together with the **Transitions** editor in Properties (go to, condition, label, order, remove, add,
  Set as start step). Canvas steps are tree items, so the tree's keyboard navigation reaches them in both views.
- The Studio raises a 1.0 file to 1.1 in the same undo step that adds a graph container, transitions or a position
  (`withGraphSchema` on every commit); it never lowers it.
- Deleting a step, or moving it out of its flowchart, also removes the transitions that went to it (and its own);
  pasted steps keep the transitions among themselves (renamed with their ids), others are dropped.

## Owner decisions (2026-10-08)
1. Schedule: after the Studio UX slice (UX-1 to UX-3), before Phase 6.
2. Format 1.1 (`transitions`, `layout`) and SDK 1.1 (`ChildLayout`, `ExecuteStepAsync`): approved.
3. State-machine transitions: conditions only for now; event/timer triggers are a later decision.
4. Build order: flowchart first, state machine second.
