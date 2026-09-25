# Web Studio

Status: Web Studio W4A (structural editing on the W3 slice). Decisions: [ADR-0021](../adr/0021-web-first-studio-and-wpf-removal.md) (web-first Studio),
[ADR-0022](../adr/0022-server-control-plane-and-project-structure.md) (server serves the Studio),
[ADR-0024](../adr/0024-execution-event-streaming-sse.md) (one event stream per tab),
[ADR-0025](../adr/0025-local-mode-security.md) (local-mode security),
[ADR-0028](../adr/0028-web-studio-first-slice.md) (W3 slice),
[ADR-0029](../adr/0029-web-studio-structural-editing.md) (structural editing and undo/redo).

The Web Studio is a React + TypeScript + Vite app in `web/studio`, outside the .NET solution. It talks only to its own
`MyRPA.Server` origin through the API in [server.md](server.md). It is not at WPF parity yet; the ADR-0021 exit
criteria are the target; the slices that get there are in [web-studio-roadmap.md](web-studio-roadmap.md).

## Run

```bash
cd web/studio
npm ci
npm run build                     # → web/studio/dist
cd ../..
dotnet run --project src/MyRPA.Server -- --project samples --web web/studio/dist --port 0
# open the printed start link (it works once)
```

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
- **Open:** choose a project and a workflow file, then Open. The file is parsed into the document model.
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
- **Run:** `POST /api/runs`. An unsaved document runs as a buffer at its path. F5 runs.
  - The run is followed on the tab's one event stream.
  - The Run panel shows the status, run id, duration, outputs, the error with its node, and every event including
    logs.
  - A refused (invalid) workflow shows its diagnostics and the status `NotStarted`.

## Structure

| File | Responsibility |
|---|---|
| `src/types.ts` | Wire types (hand-written for W3, ADR-0028 decision 6) |
| `src/api.ts` | Fetch client: anti-forgery header on state changes, ETags, error bodies |
| `src/document.ts` | Document model: immutable v1.0 JSON, client keys (`WeakMap`), index, path-copying edits, structural edits and their refusals, lossy-file detection, editability |
| `src/events.ts` | The tab's `EventSource`, subscriptions, de-duplication, stream re-creation |
| `src/store.ts` | A minimal external store with slice subscriptions |
| `src/studio.ts` | State and commands: connect, open, select, edit, insert, delete, move, undo/redo history, validate, save, run |
| `src/App.tsx` | Layout: toolbar, toolbox (Insert), edit bar (Undo, Redo, Move, Delete), tree, properties, problems and run output, status bar |
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
- `scripts/smoke.mjs`: the full demo against the real server and engine: open, insert, move (keyboard), edit, undo,
  redo, delete, undo the delete, validate, save, reload, run with events and logs, an error case, one SSE connection
  per page, and no browser errors or CSP violations. It writes screenshots to `web/studio/test-results/`.
- `scripts/perf.mjs`: measured, not asserted in CI (see ADR-0029 for the numbers).
- Architecture test `WebStudioRulesTests`: no dnd-kit or other drag-and-drop framework in `package.json` or
  `package-lock.json`.

## Deferred

- **Editing:** inserting into slots; moving between containers or slots; drag-and-drop (pointer hit-testing,
  ADR-0021); cut/copy/paste; id editing; multi-selection.
- **Editors:** the variables/arguments and workflow metadata editors; map editors; literal (number, boolean, null)
  editors; CodeMirror expression editing with live syntax feedback; assignment-target suggestions.
- **Running:** typed argument input, Stop, a timeout control, and run history.
- **Files:** create, rename, delete, save-as; recovery after a crash.
- **Round-trip:** files with comments or trailing commas; formatting preservation; every number form; duplicate JSON
  keys.
- **Tooling:** OpenAPI-generated types (ADR-0028 decision 6); the smoke test in CI, and the web job on Windows; the
  accessibility audit and screen-reader test; performance measurements in CI and on the nested fixture.
- **Out of scope:** the recorder, agents and robots, remote execution, hosted mode and authentication beyond local
  mode, plugin management UI.
