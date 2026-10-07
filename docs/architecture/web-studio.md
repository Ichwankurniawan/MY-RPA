# Web Studio

Status: Web Studio W6 (project and file management; on W3, W4A and W5). Decisions: [ADR-0021](../adr/0021-web-first-studio-and-wpf-removal.md) (web-first Studio),
[ADR-0022](../adr/0022-server-control-plane-and-project-structure.md) (server serves the Studio),
[ADR-0024](../adr/0024-execution-event-streaming-sse.md) (one event stream per tab),
[ADR-0025](../adr/0025-local-mode-security.md) (local-mode security),
[ADR-0028](../adr/0028-web-studio-first-slice.md) (W3 slice),
[ADR-0029](../adr/0029-web-studio-structural-editing.md) (structural editing and undo/redo),
[ADR-0030](../adr/0030-web-studio-execution-ux.md) (execution UX),
[ADR-0031](../adr/0031-web-studio-project-and-file-management.md) (project and file management).

The Web Studio is a React + TypeScript + Vite app in `web/studio`, outside the .NET solution. It talks only to its own
`MyRPA.Server` origin through the API in [server.md](server.md). It is not at WPF parity yet; the ADR-0021 exit
criteria are the target; the slices that get there are in [web-studio-roadmap.md](web-studio-roadmap.md).

## Run

```bash
cd web/studio
npm ci
npm run build                     # → web/studio/dist
cd ../..
dotnet build MyRPA.sln -c Release # the server's build bundles dist as its wwwroot (W6)
dotnet run --project src/MyRPA.Server -c Release --no-build -- --project samples --port 0
# or, opening one workflow (its folder becomes the project):
dotnet run --project src/MyRPA.Server -c Release --no-build -- --open samples/hello-world.json --port 0
# open the printed start link (it works once)
```

`--web web/studio/dist` still serves a specific build (for example right after `npm run build`, without rebuilding the
server).

Development with hot reload: start the server on its default port (`--port 5310`, or set `MYRPA_SERVER`), run
`npm run dev` in `web/studio`, and open the server's start link with the host and port changed to `127.0.0.1:5173`.
The dev proxy forwards `/api` and the start link; it is development-only (ADR-0028).

| Command (in `web/studio`) | What it does |
|---|---|
| `npm run typecheck` | TypeScript, strict |
| `npm test` | Vitest unit and component tests (jsdom) |
| `npm run build` | Typecheck and production build to `dist` |
| `npm run smoke` | End-to-end smoke test: the Release server (`dotnet build MyRPA.sln -c Release`) and `dist`, driven by headless Chromium on a temporary copy of `samples/hello-world.json` |
| `npm run perf` | Editing performance on the W0 3,000-node fixture against the ADR-0021 targets (same prerequisites) |

## What it does

- **Session:** the server's start link and cookie. Without a session, the Studio asks for the start link.
- **Open:** choose a project and a workflow file, then Open; or double-click a file (or Enter) in the Files panel. A
  file named with the server's `--open` opens after connecting. The file is parsed into the document model.
- **Files** (W6, ADR-0031): the Files panel shows the project's `.json` files as folders and files (click selects).
  - **New…** asks for a path (a free name is suggested) and creates a valid workflow with an empty root Sequence.
  - **Rename…** (F2) renames or moves the file in the project (server `move`, atomic, never overwrites); an open
    document follows it, unsaved edits included.
  - **Delete…** (Delete key) asks first, and says when the file is open with unsaved changes; deleting the open file
    closes it.
  - **Save as…** (toolbar) writes the document to a new file, which becomes the open file; the old file keeps its saved
    content.
  - Refusals (a taken name, a path outside the project) are shown in the dialog.
- **Unsaved changes:** opening another file with unsaved changes asks in the Studio's own dialog: Save (then open),
  Discard, or Cancel. Leaving the page still triggers the browser's own prompt.
- **Save conflicts:** if the file changed on disk since it was opened, saving offers Reload from disk, Overwrite with
  mine, or Save mine as…; the edits stay until the choice.
- **Crash recovery:** unsaved edits are kept in this browser (local storage) a second after typing pauses. Opening the
  file again offers them back (Restore, as one undoable step, or Discard), noting when the file changed on disk since.
- **Tree:** the workflow as an ARIA tree (children, then named slots), with the selected node highlighted.
  - Keyboard: ↑ ↓ Home End move the selection.
  - Badges show nodes with validation errors and each node's run state.
- **Properties:** type and id; editable display name; one editor per catalog property.
  - Required markers and descriptions are shown.
  - Validation errors appear on the property itself.
- **Toolbox:** the catalog (`/api/activities`) with a search filter. Each entry is an Insert button.
- **Structural editing** (W4A, ADR-0029):
  - Insert: after the selected activity, or at the end of a selected Sequence. The new node is `{ id, type }` with a
    unique id (`log-1`) and is selected. Nothing is inserted into slots yet.
  - Delete (button or Delete key): the selected node only; the root cannot be deleted. The next sibling, else the
    previous one, else the parent is selected.
  - Move up / Move down (buttons or Alt+↑ / Alt+↓): within the node's list; slot activities do not move.
  - Disabled commands show their reason as a tooltip; refused keyboard commands report it in the status bar.
- **Undo/redo:** Undo and Redo buttons, Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z (also in text fields). Up to 200 steps;
  typing in one property is one step; a new edit clears redo; opening a file starts a new history.
- **Validate:** `POST /api/validate`. The Problems list selects the node when a problem is clicked.
- **Save:** `PUT` with `If-Match`. A 412 keeps the edits and reports the conflict. Ctrl+S saves.
- **Dirty state:** a `•` in the title and document title; a `beforeunload` prompt while dirty. Opening another file
  asks before discarding. Undoing back to the saved version is clean again.
- **Run** (W5, ADR-0030): the Run button or F5.
  - **Configuration:** when the workflow declares In or InOut arguments, a run dialog asks for them first. Each field
    is labelled with name, type and a required marker, and shows the declared default. Text goes to the server as
    typed (`argumentText`) and is parsed there like the CLI's `--arg`. A blank field keeps the default. Start stays
    disabled, with the reason, while a required argument is blank. An optional timeout (ms) is sent as `timeoutMs`.
    The texts are remembered per file for the session.
  - **Validation first:** `POST /api/validate` runs before anything is started. An invalid workflow shows its problems
    and the run is **Not started — validation failed**; no run request is made. A request the server refuses (for
    example an argument it cannot parse) is **Not started — refused by the server**.
  - **Lifecycle:** Validating… → Waiting to start (accepted; also while queued) → Running → the engine's final status
    (Succeeded, Failed, Cancelled, Timed out). Only Validating and Not started are the Studio's own steps; everything
    after acceptance comes from the server's events. A `MYRPA2004` failure reads "Failed — arguments rejected, no
    activity ran".
  - **Stop** (button or Shift+F5): `POST /api/runs/{id}/cancel`. The run shows **Cancelling…** until the server
    reports its final state; Stop is disabled, with the reason, when the run cannot be stopped.
  - **Execution panel:** status and run id, workflow, start time, elapsed time (then duration), the node running now,
    the error with its code and node and a **Select failed node** button, outputs, a notice when the stream is
    reconnecting or events were lost (`stream.gap`), and the run's events and logs with times.
  - **Tree:** while the shown run is of the open file, nodes carry Running, Succeeded, Failed or Cancelled; nodes with
    no state were not executed and are dimmed. Only the run's own workflow maps to nodes; events of invoked workflows
    are listed only.
  - **Several runs:** Run stays available while other runs go on. Recent runs (up to 10; unfinished ones are kept)
    can be chosen in the Execution panel; each has its own status, events, logs and node states. All runs share the
    tab's one event stream.
  - **Reconnect:** the browser resumes the stream with `Last-Event-ID`; if the stream is gone, the Studio creates a
    new one and resubscribes each unfinished run after the last sequence it saw. Duplicates are ignored.
  - Streamed events are applied once per animation frame, so a burst of events costs one render.
  - The workflow stays editable during and after a run. An unsaved document runs as a buffer at its path.

## Structure

| File | Responsibility |
|---|---|
| `src/types.ts` | Wire types (hand-written for W3, ADR-0028 decision 6) |
| `src/api.ts` | Fetch client: anti-forgery header on state changes, ETags, error bodies |
| `src/document.ts` | Document model: immutable v1.0 JSON, client keys (`WeakMap`), index, path-copying edits, structural edits and their refusals, lossy-file detection, editability |
| `src/events.ts` | The tab's `EventSource`, subscriptions, de-duplication, stream re-creation, stream status |
| `src/store.ts` | A minimal external store with slice subscriptions |
| `src/drafts.ts` | Crash-recovery drafts in local storage (guarded; in memory for tests) |
| `src/studio.ts` | State and commands: connect, open, select, edit, insert, delete, move, undo/redo history, validate, save; runs (validate first, run dialog, recent runs, Stop, per-frame event batching); files (new, rename, delete, save as, unsaved prompt, conflicts, recovery) |
| `src/App.tsx` | Layout: toolbar (Save as, Run, Stop, status), Files panel, toolbox (Insert), edit bar (Undo, Redo, Move, Delete), tree, properties, problems, Execution panel, run and file dialogs, status bar |
| `scripts/harness.mjs` | Shared by the browser scripts: throwaway project, real server with `--web`, headless Chromium |
| `scripts/smoke.mjs` | End-to-end smoke test |
| `scripts/perf.mjs` | 3,000-node performance measurement |

The Web Studio owns the editing model. The server owns projects, files, validation and execution. The engine knows
nothing about the client.

## Tests

- `document.test.ts`: loading, indexing, slots, serialization without client keys, lossy-number and key-order
  detection, path-copying edits, unknown-field preservation, editability.
- `studio.test.ts`: connect and sign-in, open, select, edit and dirty state, save with If-Match and conflicts,
  validation, runs followed on one stream, events and run status, unsaved-buffer runs, refused runs, stream
  re-creation, read-only files. Uses a fake API and a fake `EventSource`.
- `App.test.tsx`: the tree, selection, keyboard navigation, editing, the dirty marker, saving, diagnostics on the
  problems list and the property, run status and events from SSE, and the signed-out view.
- `structure.test.ts`: insertion point, new nodes and ids, insert, delete, selection after delete, move, refusals,
  client keys never saved.
- `history.test.ts`: structural commands through the Studio, undo/redo of every edit kind, typing merged into one step,
  mixed histories, redo cleared by a new edit, dirty state across save/undo/redo, the 200-step cap, save and reload.
- `StructureUi.test.tsx`: command enabling and reasons, insert, delete and move by buttons and keys, focus, undo/redo
  by buttons and shortcuts, saving structural edits.
- `execution.test.ts` (W5): input arguments, the run dialog flow and `argumentText`, validation before run, not-started
  reasons, the lifecycle from server events, failures, timeouts and `MYRPA2004`, Stop (Cancelling…, 409, errors,
  refusals), node mapping (own workflow only, open file only), the event cap, concurrent runs on one stream, the
  recent-runs cap, per-frame batching, and stream re-creation with replay and gaps.
- `ExecutionUi.test.tsx` (W5): the run dialog (required, defaults, timeout, Cancel), the lifecycle and node states on
  the tree, Stop and Shift+F5, failure explanation and Select failed node, not-started labels, recent runs, the
  reconnecting and missing-events notices.
- `files.test.ts` (W6): new-workflow documents and path refusals, New (free name, create, open), the unsaved prompt
  (Cancel, Discard, Save, Save with a conflict), rename (open and other files, refusals), Save as, delete (open file
  closes; stale ETag refused), the four conflict choices, crash recovery (restore as an undoable step, stale drafts,
  identical drafts dropped, removal on save, write after a pause), and `--open` on connect.
- `FilesUi.test.tsx` (W6): the Files tree (folders, double-click, keyboard, F2, Delete), New with a refused name, the
  in-app unsaved prompt (never `window.confirm`), the conflict dialog, the recovery dialog.
- `src/test-setup.ts` clears local storage before every test.
- `scripts/smoke.mjs`: the full demo against the real server and engine (W6 scenarios: New, rename with F2, the
  unsaved prompt, a save conflict made on disk and overwritten, Save as, recovery after a page reload, delete with the
  Delete key, and a single-command start with `--open` only and the bundled Studio); open, insert, move (keyboard), edit, undo,
  redo, delete, undo the delete, validate, save, reload, run with events and logs, an error case (validated before
  run; no run request), and W5: the run dialog with typed arguments, the running view (current node, start time,
  node states), a failure with Select failed node, cancellation (Cancelling… then Cancelled), two concurrent runs, and
  a stream lost mid-run and resumed without duplicates. Also one SSE connection per page and no browser errors or CSP
  violations. It writes screenshots to `web/studio/test-results/`.
- `scripts/perf.mjs`: measured, not asserted in CI (see ADR-0029 and ADR-0030 for the numbers), including typing
  during an event-heavy run.
- Architecture test `WebStudioRulesTests`: no dnd-kit or other drag-and-drop framework in `package.json` or
  `package-lock.json`.

## Deferred

- **Editing:** inserting into slots; moving between containers or slots; drag-and-drop (pointer hit-testing,
  ADR-0021); cut/copy/paste; id editing; multi-selection.
- **Editors:** the variables/arguments and workflow metadata editors; map editors; literal (number, boolean, null)
  editors; CodeMirror expression editing with live syntax feedback; assignment-target suggestions.
- **Running:** persistent run history (only recent runs of this tab are kept); plugin load diagnostics in the Studio;
  visible (non-headless) browser runs; searching by execution or correlation id.
- **Files:** folder operations; moving files between projects; noticing outside changes before saving; recovery across
  browsers or machines.
- **Round-trip:** files with comments or trailing commas; formatting preservation; every number form; duplicate JSON
  keys.
- **Tooling:** OpenAPI-generated types (ADR-0028 decision 6); the smoke test in CI, and the web job on Windows; the
  accessibility audit and screen-reader test; performance measurements in CI and on the nested fixture.
- **Out of scope:** the recorder, agents and robots, remote execution, hosted mode and authentication beyond local
  mode, plugin management UI.
