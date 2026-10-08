# Workflow debugger — plan (proposed)

Status: **approved** by the owner on 2026-10-08 ("merge and D-1"); D-1 done, D-2 and D-3 next. Decision record:
[ADR-0040](../adr/0040-workflow-debugger.md). Not a PRD phase: an authorized follow-up slice, like the Studio UX slice and
graph workflows. Phase 7 still needs its own authorization.

## Goal
Stop a workflow where you want, step through it activity by activity, and see the values of its variables and
arguments at that moment, in the Studio, without changing the workflow file or the SDK.

## Rules
- A run without the debugger behaves and performs exactly as today (the perf targets stay).
- No workflow data in events or logs (ADR-0023): variable values are read on request by the run's own session.
- Activities, the SDK and the workflow format do not change; breakpoints are not saved in workflow files.
- Same security as runs: session, Origin and anti-forgery checks; local mode.

## Slices

| Slice | What | Done when |
|---|---|---|
| D-1 Engine and hosting | `IExecutionDebugger` hook before each node (graph steps and invoked workflows included); `DebugStop` with a read-only variable snapshot; the deadline moved by paused time; `DebugSession` in Execution.Hosting with breakpoints and Continue, Pause, Step into, Step over, Step out, Stop | Runtime tests (fake clock): pause at a breakpoint; each step command's stopping point (sequence, slots, flowchart, state machine, invoked workflow); pause on request; Stop while paused gives Cancelled; no TimedOut while paused; snapshot values. A run without a debugger is unchanged (identical event sequence) |
| D-2 Server | `POST /api/runs` with `debug`; `POST /api/runs/{id}/debug`; `PUT /api/runs/{id}/breakpoints`; `GET /api/runs/{id}/debug` (paused state with values, own session only); the `debug.paused` / `debug.resumed` events (emitted since D-1) on the stream | Server tests: start in debug, pause, step, continue, read values; another session refused; anti-forgery; events carry no values |
| D-3 Studio | Breakpoint dots on cards and canvas steps (F9), per browser per file; Debug command (F6); the debug bar (Continue F5, Pause, Step into F11, Step over F10, Step out Shift+F11, Stop); the paused card highlighted and revealed; Variables and Arguments show paused values (read-only); Execution shows where and why | Vitest; smoke: set a breakpoint, debug, pause there, read a variable, step over, continue to Succeeded; a11y with the debug bar and a paused card; perf unchanged |

## Order and delivery
D-1 → D-2 → D-3, each with its own PR, report and CI, like the earlier slices. D-1 touches only the engine (Runtime),
the Workflow contracts and Execution.Hosting. D-3 builds on the card designer and the flowchart canvas.

## Out of scope
Editing values while paused, conditional breakpoints and log points, "run to here", debugging robot or orchestrator
runs, changing a workflow while it runs.

## D-1 result (2026-10-08)

- **Workflow contracts:**
  - `IExecutionDebugger`, `DebugStop`, `DebugValue` and `DebugValueKind`;
  - `WorkflowRunRequest.Debugger`.
  - The SDK is unchanged.
- **Runtime:**
  - the hook runs before each node, before its span and `NodeStarted`;
  - depth in the whole run;
  - `DebugControl` suspends timeout timers while paused, and keeps deadlines net of paused time (`ExecutionFrame.ClockDeadline` gives `IActivityContext.Deadline`);
  - `VariableScope.Snapshot`.
- **Hosting:**
  - `ExecutionStartRequest.Debug` (`DebugOptions`: breakpoints by workflow id and node id, `PauseAtStart`);
  - `ExecutionHandle.Debug` (`DebugSession`: `Paused`, `Breakpoints`, `SetBreakpoints`, `Resume(DebugCommand)`, `RequestPause`);
  - the `debug.paused` / `debug.resumed` events (Contracts: two kinds and an additive `Reason`).
  - Stop is `ExecutionHost.Cancel`.
- **Tests:**
  - Runtime `DebuggerTests` (6): every node with its depth (sequence, nesting, graph steps, invoked workflow); a debugger that never pauses leaves the events identical; pause before a node with its values; pause not counted by the timeout; the timeout resumes with its remaining time; cancel while paused.
  - SDK: `context.Deadline` moves by the paused time.
  - Hosting `DebugSessionTests` (9): breakpoint, with the events free of values; step into, over and out; flowchart steps; a breakpoint in an invoked workflow and step out back to the invoker; a ForEach item as a local; pause requested during a running activity; breakpoints changed while paused; cancel while paused; no session or events without debug.
