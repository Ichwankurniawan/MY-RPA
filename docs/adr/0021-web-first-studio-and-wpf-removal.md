# ADR-0021: Web-first Studio; the WPF Studio is temporary and will be removed

- Status: Accepted
- Date: 2026-09-25
- Phase: Web Studio W0
- Supersedes: [ADR-0018](0018-studio-architecture.md) for the long-term Studio direction
- Amends: PRD 5.1 and the PRD technology table ("Studio: WPF")
- Related: [ADR-0022](0022-server-control-plane-and-project-structure.md) (server and projects),
  [ADR-0023](0023-first-class-execution-events.md) (execution events),
  [ADR-0024](0024-execution-event-streaming-sse.md) (streaming),
  [ADR-0025](0025-local-mode-security.md) (local-mode security).
  The spike is described in [W0 spike report](../research/w0-web-studio-spike.md).

## Context
Phase 5 delivered a working WPF Studio (ADR-0018). Future work will target the browser:
- remote execution;
- Agents and Robots;
- the Phase 6 recorder;
- AI/MCP;
- orchestration.

Keeping two UIs would double the cost of every feature. ADR-0018 assumed that a web Studio would reuse the Studio.Core view models. A React client can't do that.

## Decision
- **The Web Studio is the only Studio UI.**
  - Stack: React + TypeScript + Vite, in `web/studio`, outside the .NET solution.
  - It is the authoring and control UI. It talks only to its own `MyRPA.Server` origin (ADR-0022), never to a localhost execution API or an agent.
- **The v1.0 workflow JSON is the canonical and only format.**
  - The web client edits it directly as an immutable tree.
  - Unknown fields and literal forms are preserved.
  - There is no web-specific format and no .NET "authoring layer". Server-side validation with `WorkflowLoader` is authoritative.
- **The WPF Studio (`MyRPA.Studio`, `MyRPA.Studio.Core` and their tests) is frozen.**
  - It is a temporary behavioral reference.
  - It gets no features and no refactoring to share code with the new layers, and it is not a design constraint for anything new.
  - It is deleted in one pull request once every exit criterion below passes and the owner signs off the exit review.
- **Carried forward from Phase 5:**
  - the format and loader;
  - the catalog snapshot (ADR-0020);
  - the plugin configuration file (ADR-0019);
  - provider verification;
  - behaviors used as the specification for the web port: edit rules, id generation, paste renaming, diagnostic mapping, and the regression cases for pending edits and closing.
- **Not carried forward:**
  - view models and UI service interfaces;
  - `DocumentHistory` and `DraftClipboard` (the web has its own client-side undo and clipboard);
  - span-based run monitoring (replaced by ADR-0023).

### Frontend baseline (confirmed by the W0 spike)
- A hierarchical nested-block designer, not a node graph.
- Immutable document updates with structural sharing; blocks are memoized on node identity.
- Selection and diagnostics are per-block subscriptions.
- Blocks use **stable client-side keys that are never saved**. Index or path keys make an insert at the top of a list re-render every later sibling: measured 81 ms against 17 ms for an insert at the end.
- **dnd-kit is rejected.**
  - Its context re-renders every draggable and droppable on each edit: measured 58 ms against 17 ms per edit at 3,001 nodes. It also doubles the JS heap (125 MB against 48 MB) and costs 240–290 ms on drag activation.
  - Its keyboard sensor did not complete drops in a nested document.
  - Its default attributes conflict with the ARIA tree pattern.
  - Use plain pointer hit-testing instead, with a tree-aware "nearest valid drop position" rule, plus keyboard commands (move up/down, cut/paste, insert at selection).
- **CodeMirror 6 for expressions.** Mount time is about 1.2 ms at p50. Each keystroke updates the document and is merged into one undo step, so there is no "typed but not committed" state.
- Snapshot undo/redo on the client, at least 200 steps.
- One multiplexed event stream per browser tab (ADR-0024).

## Exit criteria for deleting WPF
All items must pass in the Web Studio. Functional items can only change with the owner's explicit approval.

1. **Authoring**
   - Create, open, save and save-as within a project.
   - Insert any catalog activity, including plugin activities and all `Browser.*`, both by drag-and-drop and by keyboard.
   - Move, nest and delete.
   - All slot kinds, including named `case:` slots.
   - Unique id generation; editable id and display name.
   - Unknown activities shown with editable raw properties.
   - Lossless round-trip of unknown fields and literal forms on the shared corpus.
2. **Property editing**
   - All six property kinds, with required markers and descriptions.
   - Allowed-value lists, assignment-target suggestions, the key/value map editor, and live expression syntax feedback.
   - No "typed but not committed" state (the Phase 5 regression cases are ported).
3. **Variables and arguments:** add, remove, rename; type and direction; required; defaults as JSON with inline errors.
4. **Validation**
   - Every `WorkflowLoader` diagnostic shown on its block, property, row or on the workflow, with an Errors list that navigates to it.
   - Corpus parity with Phase 5 `DraftValidator` locations for every MYRPA10xx code.
   - A missing required property is reported on the property itself.
   - Validation never blocks typing.
5. **Undo/redo:** at least 200 steps; typing merged into one step; every edit kind covered; dirty state correct, including after undoing back to the saved state.
6. **Copy/paste:** cut, copy and paste within and across documents, with id renaming; keyboard paste without a clipboard permission prompt.
7. **Execution**
   - Run with typed argument input; Stop; a timeout control.
   - Status and outputs; errors with the failing node; per-node running, completed and failed states.
   - Running an unsaved buffer with correct sub-workflow resolution; works across reconnects.
8. **Logs, output and errors:** logs show level and node id, bounded, with clear; execution and correlation ids visible; no stack traces or internals shown.
9. **Projects and files**
   - Open a project folder, file tree, create/rename/delete workflows.
   - Conflict detection on save.
   - Unsaved-changes protection (in-app prompt, `beforeunload`, local crash recovery).
   - Command-line equivalents of the WPF startup options.
10. **Keyboard and accessibility**
    - Every operation possible without a mouse.
    - Focus-safe shortcuts: Ctrl+S, Z, Y, X, C, V, Del, F5, Shift+F5.
    - ARIA tree semantics and announced run status.
    - An automated accessibility check with no serious or critical violations, plus a screen-reader smoke test.
11. **Performance**, on the 3,000-node reference fixtures, measured in CI:
    - open to interactive ≤ 1 s;
    - property edit to paint ≤ 100 ms (p95);
    - undo ≤ 100 ms;
    - validation result ≤ 500 ms after typing stops (local mode).
    - W0 recommends tighter targets; see the spike report.
12. **Automated tests**
    - TypeScript unit tests covering every behavior group of the Studio.Core tests.
    - Corpus conformance against `WorkflowLoader`.
    - Server API, security and SSE tests.
    - End-to-end tests covering the PRD 5.5 definition of done and every step of the Studio manual test script.
    - CI green on Linux and Windows.
13. **Former WPF-only capabilities**
    - Clear reporting of plugin load failures at startup.
    - Title and dirty marker; a status summary.
    - Visible (non-headless) browser runs in local mode.
    - A single-command local start.
14. **Process:** docs updated; a human has run the manual script once; the owner signs off the exit review.

## Consequences
- The PRD's "Studio: WPF" no longer holds. This ADR records that product decision.
- After deletion:
  - the whole .NET solution is plain `net10.0` again, and WPF is forbidden everywhere;
  - the `IsDesktopUi` exemption, the linked-detector test setup, `CommunityToolkit.Mvvm`, and the CI step that builds Studio on Linux / tests it on Windows go away.
- Files are project-based. The browser can't open arbitrary files.
- "Save changes?" becomes an in-app prompt plus recovery, because browsers only allow a generic leave-page prompt.
- A new toolchain (Node/npm) is needed to build, but not to run: the server serves the built assets.
- The Phase 6 recorder UI waits for the Web Studio MVP. Recorder backend work (browser contracts, item I2) can proceed in parallel after the server exists.
- 111 Studio tests are deleted with WPF. Criterion 12 requires equivalent coverage first.
