# ADR-0030: Web Studio execution UX (W5)

- Status: Accepted for W5
- Date: 2026-10-07
- Phase: Web Studio W5 (authorized before W4B)
- Builds on: [ADR-0023](0023-first-class-execution-events.md) (execution events), [ADR-0024](0024-execution-event-streaming-sse.md)
  (one SSE stream per tab), [ADR-0025](0025-local-mode-security.md) (local security),
  [ADR-0028](0028-web-studio-first-slice.md) (W3 slice)

## Context
W3 could start a run and show its events. W5 makes running usable: run configuration with arguments, validation
before run, a clear lifecycle including Not started and Cancelling…, Stop, live node states, understandable failures,
several runs at once, and resume after a dropped stream. The engine, Contracts, Execution.Hosting, the event model and
the stream architecture are unchanged: cancellation (`POST /api/runs/{id}/cancel`), timeouts (`timeoutMs`), run state
and replay already existed.

## Decision
1. **Arguments are typed as text and parsed by the server.** `POST /api/runs` gains `argumentText`
   (`{ name: text }`), parsed with `WorkflowValues.TryParseText`, the rule of the CLI's `--arg` and the WPF run
   prompt. The browser never converts values: JSON from React would have re-implemented per-type rules (String
   verbatim, invariant numbers and dates, JSON for lists) and lost precision for large Int values. The change is
   additive; `arguments` (JSON values) is unchanged; a name may not be in both. The run dialog reads the argument
   definitions from the open document, checks only the `required` flag, and sends blanks as absent so the engine
   applies the default.
2. **Validate, then run.** Run calls `POST /api/validate` first; an invalid workflow is never submitted and is shown as
   *Not started — validation failed* with its problems. A request the server refuses (400, or a 422 race) is
   *Not started — refused by the server*. These are the only outcomes the Studio decides itself.
3. **No client-side execution states.** A run's status is the Studio's own Validating or Not started until the server
   accepts it, then *Starting* (shown as "Waiting to start", which covers the server's Queued) until the engine
   reports it, then the server's Running and the final status of `execution.completed`. Stop sets only a
   `cancelRequested` flag, shown as *Cancelling…*; Cancelled is shown only when the server reports it. The `MYRPA2004`
   argument rejection keeps the engine's Failed status, with a clearer label.
4. **One model per run.** The Studio keeps up to 10 recent runs (unfinished ones are never dropped), each with its own
   events (latest 1,000), logs, node states and running-node list, routed by run id on the tab's one stream. Run stays
   available during other runs. Tree badges come from the shown run only when it ran the open file, and only from the
   run's own workflow (events without a parent execution); invoked workflows' events are listed, never mapped to nodes.
5. **Events are applied per animation frame.** Streamed events are queued and applied in one store update per frame,
   at most 250 per frame, so a burst costs a few renders instead of one per event. Event rows are memoized on the
   event object. Measured, not assumed: without batching, the 3,001-node fixture's run (about 8,700 events) re-rendered
   the event list once per event and froze the page for minutes (`npm run perf` did not finish in 10 minutes).
6. **Stream status is visible.** The stream reports `connected` (the server's `stream.opened`, also after the
   browser's own reconnects) and `reconnecting`; the panel shows a notice while reconnecting and counts events the
   server no longer had (`stream.gap`).

## Consequences
- ADR-0021 exit criterion 7 (execution) is covered: typed argument input, Stop, timeout, and reconnect proven end to
  end in the smoke test (a stream deleted mid-run is re-created and resumed without duplicates or loss). Clearing the
  log and showing execution and correlation ids (criterion 8) remain open.
- Measured with `npm run perf` on 2026-10-07. The workstation was much slower than on 2026-09-25: unchanged `main`
  measured keystroke p95 50–95 ms during the day. Interleaved A/B runs of `main` and W5 on the same machine:
  - keystroke p95 95.0 / 84.6 ms (main) vs 99.7 / 98.2 ms (W5); script p50 42.2 / 42.0 vs 44.2 / 47.0 ms;
  - undo p95 82.2 / 87.0 vs 74.7 / 79.2 ms; insert and delete within ±5 ms.
  So there is no regression beyond the run-to-run spread, with a possible 2–5 ms more script per keystroke. The
  ADR-0021 targets could not be confirmed on this machine that day, for `main` as well as W5; they should be
  re-measured on a quiet machine.
- Typing during the event-heavy run: p95 64–173 ms across runs on that machine (target 50; plain typing measured
  85–100 ms there at the same time, so this is not confirmed as a W5 cost either). Without typing, the Studio rendered
  frames at about 19 ms while the run's events arrived. A one-off main-thread task of 0.7–0.9 s at the start of the
  run came from dimming not-executed nodes with `opacity` (3,000 nodes restyled at once); with a muted text color
  instead, the longest task was 194 ms.
- **Found in W5, fixed right after it** (owner decision; ADR-0024 amended): the pump now waits for queue room and only
  a client that takes no event for 10 s is dropped. The 8,700-event run then reached the Studio in 2.6 s instead of
  40.8 s. The original finding: the run's whole event stream reached the Studio only after about 40 s, although
  the engine finished in about 0.1 s. When a stream subscribes to a run, its pump replays every retained event into the
  connection queue with `TryWrite`. The queue holds 4,096 events (`StreamQueueCapacity`) and a run retains up to
  10,000 (`EventBufferCapacity`). A run with more retained events therefore overflows the queue at once, whatever the
  client's speed: the server drops the connection, the browser waits `retry: 2000` and resumes by cursor, and the cycle
  repeats. Nothing was lost or duplicated, but large runs arrived in 2-second steps.
- Completed on 2026-10-08 (W5 gap closing, before W7): plugin load diagnostics in the Studio (`GET /api/plugins`, a
  notice above the designer and the loaded plugins under the catalog), Clear log, execution and correlation ids, and the
  failing activity's type and error kind. Visible browser runs need no new option: the browser plugin's `headless`
  setting (via `--plugin-config`) and `Browser.Open`'s `headless` property already provide them, as in the WPF Studio.
- Deferred: persistent run history (post-Phase-5).
