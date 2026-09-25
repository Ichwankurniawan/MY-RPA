# W0 Web Studio spike: measurements and findings

- Date: 2026-09-25
- Code: [`spikes/w0-web-studio`](../../spikes/w0-web-studio/README.md). Throwaway; not production, not in `MyRPA.sln`.
- Decisions informed: ADR-0021 to ADR-0025

## Environment and method
- **Machine:** AMD Ryzen AI 7 350 (16 logical cores), 31 GB RAM, Windows 11. Server and browser on the same machine.
- **Browser:** headless Chromium 153 (Playwright 1.63 build), 1440×900 viewport, driven by a runner outside the repository.
  - Headless Chromium uses software rasterization, so real desktop browsers are likely equal or faster.
  - An attempt in the desktop app's built-in browser pane was discarded: the pane was hidden or resized during the run, and that throttles animation frames.
- **Frontend:** React 19.3, Vite 7.3, TypeScript 5.9, CodeMirror 6 (`@codemirror/view` 6.43), `@dnd-kit/core` 6.3.1. Production build: 481 KB of JS, 155 KB gzipped.
- **Server:** an ASP.NET Core (.NET 10) minimal API using the real engine: `WorkflowLoader`, `WorkflowRunner`, built-in activities.
- **Documents:**
  - *flat*: the Phase 5 baseline shape, 3,001 nodes (root Sequence, 300 groups of Sequence + 9 Log), 217 KB;
  - *nested*: 3,301 nodes with TryCatch and If slots, depth up to 7, 236 KB.
- **Timing:** each measurement runs from the action to the next painted frame (`requestAnimationFrame` followed by a task), so the floor is one frame, about 16.7 ms.
- **Repetitions:** 3 runs per configuration, each with 20–50 samples per metric. The tables show the median of the per-run p50 and p95 values. Pseudo-random targets use a fixed seed.
- React's `Profiler` reports nothing in production builds, so no React commit times are reported.

## Designer results (3,000-node documents)

| Metric (ms) | flat, dnd-kit | flat, pointer hit-testing | nested, dnd-kit | nested, hit-testing |
|---|---|---|---|---|
| Open (parse + first render + paint) | 317 | **121** | 269 | **154** |
| Select a block, p50 / p95 (includes mounting CodeMirror) | 17.4 / 27.9 | 17.1 / 27.3 | 17.5 / 30.9 | 16.5 / 28.0 |
| CodeMirror mount, p50 / p95 | 1.4 / 11.6 | 1.2 / 13.5 | 1.4 / 13.4 | 1.2 / 12.2 |
| Property edit to paint, p50 / p95 | 58.3 / 81.9 | **16.7 / 18.3** | 62.6 / 88.7 | **16.8 / 18.2** |
| Keystroke (CodeMirror) to paint, p50 / p95 | 57.9 / 81.2 | **16.6 / 18.0** | 58.4 / 87.7 | **16.6 / 18.0** |
| Undo, p50 / p95 | 56.6 / 78.1 | 16.8 / 18.3 | 59.0 / 84.1 | 16.6 / 18.7 |
| Redo, p50 / p95 | 57.3 / 77.3 | 16.6 / 19.1 | 62.2 / 79.7 | 16.7 / 18.0 |
| Keyboard move (Alt+Down, real key event), p50 / p95 | 60.3 / 81.3 | 15.1 / 17.4 | 57.4 / 82.4 | 13.8 / 17.0 |
| Insert at top of root list, p50 / p95 | 93.7 / 121.2 | 81.3 / 103.6 | 90.2 / 120.8 | 68.4 / 82.8 |
| Insert at end of root list, p50 / p95 | 54.3 / 65.8 | **16.9 / 18.5** | 60.6 / 88.1 | **16.7 / 19.1** |
| Delete at top, p50 / p95 | 96.0 / 122.5 | 79.4 / 111.6 | 84.7 / 113.3 | 68.4 / 81.4 |
| Drag activation | 292 | **15** | 243 | **14** |
| Drag move to paint, p50 / p95 | 14.1 / 96.7 | 15.8 / 38.7 | 18.4 / 100.1 | 16.8 / 38.8 |
| Drop to paint | 144 | 31 | 152 | 16 |
| JS heap (MB) | 125 | **48** | 179 | **48** |
| DOM elements | 18,350 | 18,348 | 21,950 | 21,948 |

**Other results:**
- **Typing and undo:** 50 keystrokes were merged into one undo step in every run.
- **Keyboard moves** were applied 30/30 (flat) and 27/30 (nested); the 3 no-ops were blocks that were already last in their list.
- **dnd-kit's keyboard sensor** completed the drop in the flat document but **never in the nested one** (0 of 3 runs).
- **Pointer hit-testing in the nested document didn't drop.** The pointer ended over a block rather than a drop zone. A production designer needs a "nearest valid position" rule.

**Validation round trip** (browser to `/api/validate` to `WorkflowLoader` and back; serialization about 0.7 ms):

| | Round trip, p50 / p95 (ms) | Server time, p50 (ms) | Body |
|---|---|---|---|
| flat | 9.5 – 10.5 / 12.3 – 12.5 | 5.3 – 6.1 | 217 KB |
| nested | 12.7 – 12.9 / 14.9 – 17.4 | 8.1 – 8.3 | 236 KB |

**Comparison with the Phase 5 WPF baseline** at 3,001 nodes (view models only, no rendering): 116 ms to open and about 93 ms per property commit. The web spike, *including rendering*, took 121 ms to open and 17–18 ms per edit (p95), without dnd-kit.

## SSE prototype results
Native `EventSource`; the server uses `TypedResults.ServerSentEvents`; 2 runs.

| Test | Result |
|---|---|
| 200-node workflow (604 events) | 4–16 ms total, contiguous from the start, status Succeeded |
| 2,000-node workflow (6,004 events) | 42–46 ms total, about 130,000–144,000 events per second, contiguous |
| Replay: read 1,000 events, drop, resume with `Last-Event-ID` | 1,000 + 5,004 = 6,004 events, no gaps, no duplicates |
| Cancel a 20 s Delay after 300 ms | Cancelled; completion event 3–16 ms after the cancel request |
| Other request (`/api/ping`) with 0 / 5 / 6 open streams | 1.2–1.4 ms / 1.8–1.9 ms / **blocked for more than 3 s** |
| Same, with 1 multiplexed stream for 6 runs | 2.7–4.1 ms |
| Protocol | `http/1.1` (plain HTTP on loopback) |

## Span-listener capture cost (server side)
20 concurrent runs of a 1,000-node Assign workflow; median of 3 repetitions. The prototype's always-on shared listener was active in every mode, so "none" means no *additional* listener.

| Mode | Total (ms) | µs per node |
|---|---|---|
| none (hub listener only) | 16.2 | 0.81 |
| one shared dispatching listener | 9.6 | 0.48 |
| one listener per run (Phase 5 `RunMonitor` pattern) | 22.9 | 1.14 |

The per-node cost is small in every mode, so performance is not what argues against span-based events; correctness and isolation are (ADR-0023). The single-run numbers (3–5 ms) are too noisy to compare.

## Findings
1. **React + TypeScript is confirmed.** Without dnd-kit, every edit kind paints within one frame at 3,000+ nodes, far inside the targets.
2. **Reject dnd-kit.** It adds about 40 ms to *every* edit (its context re-renders all ~6,300 draggable and droppable hooks), doubles or more the heap, costs 240–290 ms on drag activation, fails keyboard drops in nested trees, and its default ARIA attributes conflict with the tree pattern. Plain hit-testing costs 14–15 ms to activate and 39 ms at p95 per move.
3. **Use stable, never-saved client keys.** With index or path keys, an insert or delete at the top re-renders every later sibling (68–81 ms against 17 ms at the end).
4. **Use one multiplexed SSE stream per tab.** Six streams exhaust HTTP/1.1's connections per origin and block the page.
5. **Server validation is fast enough** to run on every change (debounced) in local mode: 12–17 ms round trip at p95.
6. **.NET 10 has native SSE** (`TypedResults.ServerSentEvents`, `SseItem.EventId`); no extra package is needed.
7. **Per-execution log routing** through the engine's logging scope keys works without engine changes.

## Recommended performance targets (for ADR-0021 criterion 11; numbers only)
| Metric | Initial target | Recommended | Measured without dnd-kit |
|---|---|---|---|
| Open to interactive (3,000 nodes) | ≤ 1 s | ≤ 1 s (keep) | 121–154 ms |
| Property edit or keystroke to paint, p95 | ≤ 100 ms | **≤ 50 ms** | 18 ms |
| Undo/redo, p95 | ≤ 100 ms | **≤ 50 ms** | 18–19 ms |
| Structural edit (insert/delete/move) to paint, p95 | (not separate) | **≤ 100 ms** | 17–19 ms with stable keys expected; 81–112 ms with index keys |
| Drag activation / move, p95 | (not separate) | **≤ 100 ms / ≤ 50 ms** | 14–15 / 39 ms |
| Validation result after typing stops | ≤ 500 ms | ≤ 500 ms (keep; covers remote) | 12–17 ms locally |

## Caveats
- One machine; headless Chromium; server and client on the same machine.
- The spike renders every block with no virtualization or collapsing. The production designer should still collapse blocks for navigation, but performance doesn't require it at this size.
- The structural-edit figure with stable keys is expected, not measured. It must be verified in W3.
