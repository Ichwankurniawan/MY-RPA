# ADR-0040: Workflow debugger — breakpoints, pause and stepping

- Status: **Accepted** (owner, 2026-10-08: "merge and D-1"). Not part of a PRD phase: the PRD's vision lists a
  "Debugger" and the Phase 5 Studio mock-up a "Debug" menu, but no phase defines one. It is an authorized
  follow-up slice like the Studio UX slice and graph workflows (ADR-0037).
- Builds on: ADR-0010 (execution model), ADR-0022 and ADR-0023 (hosting and events), ADR-0024 (the event stream),
  ADR-0037 (graph steps are nodes too).

## Context
Today a workflow can be followed (live node states, logs, the failed node, the arrow taken) but not stopped mid-way:
there are no breakpoints, no pause, no stepping, and no view of variable values during a run. Developers debug by
adding `Core.Log` activities. A debugger needs the engine to wait before a node, which it cannot do now.

Constraints that shape it:
1. A run without a debugger must not change (no extra awaits, no extra events, the perf targets hold).
2. Events carry no workflow data (ADR-0023): variable values never go into the event stream or logs.
3. Activities and the SDK are unchanged: an activity is never told it is being debugged.
4. One workflow model: breakpoints are not workflow data and are not saved in the workflow JSON.

## Decision

### 1. Engine: an optional debug hook (MyRPA.Workflow contracts, MyRPA.Runtime)
- `WorkflowRunRequest.Debugger` (optional `IExecutionDebugger`). When it is null, nothing changes.
- The runner awaits `IExecutionDebugger.BeforeNodeAsync(DebugStop, CancellationToken)` before each node starts,
  including flowchart and state-machine steps and the nodes of invoked workflows (their execution identity tells them
  apart). `DebugStop` carries the node id and type, the call depth and frame, and a **read-only snapshot accessor**
  of the variables and arguments in scope (canonical values, read only on request).
- The hook may wait as long as it likes (that is the pause); the run's cancellation token still ends it.
- Timeouts: a debug run's timeout clock stops while it is paused (the deadline moves by the paused time), so a pause
  never turns into `TimedOut`.

### 2. Hosting: a debug session per run (MyRPA.Execution.Hosting)
- `ExecutionStartRequest.Debug` starts a run with a `DebugSession`: breakpoints (node ids, per workflow) and the
  commands **Continue**, **Pause**, **Step into** (pause before the next node at any depth), **Step over** (the next
  node at the same or a shallower depth), **Step out** (the next node shallower than the current one) and **Stop**
  (cancel).
- A breakpoint is a workflow id and a node id, so nodes of invoked workflows can have them too. `PauseAtStart` pauses
  before the first node (Step into before the run begins).
- While paused, the session holds the paused node and a copy of the values taken at the pause; the state is served on
  request.
- Pausing and resuming append `debug.paused` (`reason`: `breakpoint`, `step` or `pause`) and `debug.resumed` (the
  command) to the run's events, which carry only ids (the additive `reason` field of the event message). They are emitted
  by the session, so they exist for every host; D-2 serves them on the existing stream.

### 3. Server (MyRPA.Server)
- `POST /api/runs` gains `debug: { breakpoints: [nodeId…] }` (same validation and security as runs).
- `POST /api/runs/{id}/debug` with `{ command }`; `PUT /api/runs/{id}/breakpoints` replaces the set while running.
- The `debug.paused` / `debug.resumed` events reach clients on the existing stream (§2).
- `GET /api/runs/{id}/debug` returns the paused state **with the variable values**, only to the session that started
  the run (local mode), and is never logged.

### 4. Studio
- Breakpoints: a dot on each card and canvas step (F9 toggles), kept per browser per file (preferences), never in
  the workflow file.
- **Debug** (F6) runs with breakpoints; a debug bar shows Continue (F5), Pause, Step into (F11), Step over (F10),
  Step out (Shift+F11) and Stop (Shift+F5), each with its disabled reason.
- When paused: the paused card is highlighted and revealed (a step inside a flowchart opens it), and the Execution
  panel shows where and why it paused, with the values in scope (read-only). (Built in D-3; the plan first named the
  Variables and Arguments tabs, which edit declarations.)

## Alternatives considered
- **Breakpoints as a workflow field** (saved in the JSON): rejected. They are a developer's working state, and would
  change the format and every file's diff.
- **Pausing inside activities** (an SDK hook): rejected. Activities stay unaware of debugging, and the SDK does not
  change.
- **Variable values in the event stream**: rejected by ADR-0023. They are fetched on request by the run's own session.

## Consequences
- The engine gains one optional await per node, for debug runs only; normal runs are measured unchanged.
- The SDK and the workflow format do not change.
- Not in this decision: editing values while paused, conditional breakpoints and log points, "run to here",
  debugging runs on robots or an orchestrator, changing a workflow while it runs.
