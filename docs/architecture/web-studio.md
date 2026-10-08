# Web Studio

Status: Phase 5 complete (W3–W10; the WPF Studio is archived, ADR-0036). The Studio of MyRPA. Decisions: [ADR-0021](../adr/0021-web-first-studio-and-wpf-removal.md) (web-first Studio),
[ADR-0022](../adr/0022-server-control-plane-and-project-structure.md) (server serves the Studio),
[ADR-0024](../adr/0024-execution-event-streaming-sse.md) (one event stream per tab),
[ADR-0025](../adr/0025-local-mode-security.md) (local-mode security),
[ADR-0028](../adr/0028-web-studio-first-slice.md) (W3 slice),
[ADR-0029](../adr/0029-web-studio-structural-editing.md) (structural editing and undo/redo),
[ADR-0030](../adr/0030-web-studio-execution-ux.md) (execution UX),
[ADR-0031](../adr/0031-web-studio-project-and-file-management.md) (project and file management),
[ADR-0032](../adr/0032-web-studio-rich-authoring.md) (rich authoring),
[ADR-0033](../adr/0033-web-studio-parity-authoring.md) (slots, moves, clipboard, drag-and-drop),
[ADR-0034](../adr/0034-web-studio-quality-review.md) (performance, accessibility, security),
[ADR-0035](../adr/0035-web-studio-wpf-exit-review.md) (corpus parity and the WPF exit review).

The Web Studio is a React + TypeScript + Vite app in `web/studio`, outside the .NET solution. It talks only to its own
`MyRPA.Server` origin through the API in [server.md](server.md). The parity evidence, the known
differences, the manual test script and the exit sign-off are in [web-studio-parity.md](web-studio-parity.md); the
slices are in [web-studio-roadmap.md](web-studio-roadmap.md).

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
| `npm run perf` | Performance on the W0 3,000-node fixture against the ADR-0021 targets: open, typing, undo/redo, structural edits, drag activation and movement; typing during an event-heavy run (informational). `MYRPA_PERF_TOLERANCE` scales the targets on slower machines (CI: 2). Same prerequisites |
| `npm run a11y` | Accessibility (W8, ADR-0034): axe-core in 9 states (light and dark), failing on serious or critical violations, plus keyboard-only authoring. Same prerequisites |

## What it does

- **Shell (UX-1):** a command bar with File (New workflow…, Save, Save as…), Edit (Undo, Redo, Cut, Copy, Paste, Delete,
  Move up/down), Run (Validate, Run, Stop, run status) and View (Theme: System, Light, Dark; remembered per browser)
  groups. Each command shows an icon from the MyRPA icon set (`src/icons.tsx`, drawn for the project) and its label,
  which is its accessible name; a disabled command's tooltip says why. The workflow's name and description head the
  designer. The status bar shows the connection, the open file (saved, unsaved, read-only), validation and the current
  run, then the announced message. The favicon is the MyRPA mark.
- **Panels (UX-2):** the activity panel lists **Favorites** (star an activity) and **Recent** (the last 10 inserted)
  first, then the catalog by namespace (Built-in, then plugin namespaces) and category; Favorites and Recent entries
  insert and drag like the catalog's, with their own accessible names ("Log (Core.Log) from Recent"). The bottom panel
  is one tab strip: Problems (n), Variables (n), Arguments (n), Execution; a run shows Execution, Validate (or a run
  refused by validation) shows Problems, background validation never switches the tab. Splitters on the panel edges
  resize the activities, properties and bottom panels (drag, or focus and use the arrow keys; Home/End), and View ›
  Activities / Properties / Bottom hides or shows them. Favorites, recent activities and panel sizes are remembered per
  browser (`src/preferences.ts`, best effort).
- **Designer (UX-3):** activities are cards in a centred flow with connectors; each card has an icon header (type icon,
  display name, slot, badges). The selected card edits its properties inline (the same editors as the Properties
  panel) and has a ⋮ menu (Cut, Copy, Paste, Delete, Move up/down); other cards show a one-line summary of their
  values. Containers collapse and expand (the card's toggle, ArrowLeft/ArrowRight, Expand all / Collapse all); a
  selection inside a collapsed container expands it. Zoom 50–200 % with Ctrl+= / Ctrl+- / Ctrl+0, the zoom buttons
  and fit to width (CSS `zoom` set through the CSSOM). While dragging, one transparent overlay carries the cursor.
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
- **Designer:** the workflow as an ARIA tree of activity cards (children, then named slots); containers have a
  boundary; empty lists and missing slots are named in the card. Breadcrumbs (Workflow › … › selection) select any
  ancestor or the workflow itself.
  - Keyboard: ↑ ↓ Home End move the selection.
  - Badges show nodes with validation errors, each node's run state, and activities missing from the catalog.
- **Properties** (W4B, ADR-0032): for an activity, its type, editable id, display name and one editor per catalog
  property kind:
  - Expression: text (a literal number, boolean or null is shown as JSON and marked *literal*);
  - Text: text, or a choice list when the catalog names allowed values;
  - AssignmentTarget: text with suggestions (variables, Out/InOut arguments); LocalName: text;
  - ExpressionMap / AssignmentTargetMap: entries (name → value) with add, remove and in-place refusal of a blank or
    duplicate name.
  - Required markers, descriptions and validation errors on the property itself.
  - Properties the catalog does not describe (all of an activity missing from the catalog) are raw JSON, stored only
    while valid, with add and remove.
  - For the workflow (first breadcrumb): id, name, version, description.
- **Variables and Arguments** (tabs beside Problems): add, remove, rename; type; direction and required (arguments);
  default as JSON, stored only while valid. An Out argument has no required flag or default. Row problems show on the
  row.
- **Live validation:** the server validates once typing pauses (300 ms) and the results appear on nodes, properties,
  rows and the workflow; validation never blocks typing.
- **Toolbox:** the catalog (`/api/activities`, built-in and plugin activities) grouped by category (collapsible), with a
  search over names, types, categories and descriptions. Each entry is an Insert button.
- **Themes:** light and dark, following the system setting.
- **Slots, moves, clipboard and drag-and-drop** (W7, ADR-0033):
  - Empty slots and empty lists show inside their card as buttons: clicking one picks it for the next insert or paste.
    A Switch has a "+ case:" zone that takes the case value. Without a picked place, an insert or paste goes into the
    selected node's list, else its first empty slot, else after it.
  - Cut, Copy, Paste (buttons, Ctrl+X/C/V outside text fields): the clipboard holds readable workflow JSON
    (`{"myrpaNodes":"1.0","nodes":[…]}`); pasting renames ids already in use; it works across documents and tabs.
  - Drag an activity card to a gap between cards (upper or lower half) or onto an empty-slot or empty-list zone to move
    it, also across containers; drag a toolbox entry to insert. A refused place shows the not-allowed cursor; Esc
    cancels. Each drop is one undo step.
- **Structural editing** (W4A, ADR-0029):
  - Insert: after the selected activity, or at the end of a selected Sequence. The new node is `{ id, type }` with a
    unique id (`log-1`) and is selected. Nothing is inserted into slots yet.
  - Delete (button or Delete key): the selected node only; the root cannot be deleted. The next sibling, else the
    previous one, else the parent is selected.
  - Move up / Move down (buttons or Alt+↑ / Alt+↓): within the node's list; slot activities do not move.
  - Disabled commands show their reason as a tooltip; refused keyboard commands report it in the status bar.
- **Undo/redo:** Undo and Redo buttons, Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z (also in text fields). Up to 200 steps;
  typing in one property is one step; a new edit clears redo; opening a file starts a new history.
- **Validate:** `POST /api/validate` (also automatic, above). The Problems list goes to where a problem belongs: its
  node, its argument or variable row, or the workflow.
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
    execution and correlation ids, the error with its code, node, activity type and error kind and a **Select failed
    node** button, outputs, a notice when the stream is reconnecting or events were lost (`stream.gap`), and the run's
    events and logs with times. **Clear log** empties the list for that run (the run is unaffected).
  - **Plugins:** plugins that failed to load (optional ones; a required one stops the server) are reported in a notice
    above the designer; the loaded plugins are listed under the activity catalog.
  - **Visible browser runs:** `Browser.Open` has a `headless` property, and the browser plugin's `headless` setting
    is its default; set it to `false` in the plugin configuration file given to the server with `--plugin-config` (as
    the WPF Studio did). No Studio option is needed.
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
| `src/preferences.ts` | Per-browser UI preferences (UX-2): favorites, recent activities, panel sizes; guarded storage, in-memory store for tests |
| `src/icons.tsx` | The MyRPA icon set (UX-1, UX-3): 47 icons on a 24 × 24 grid, `currentColor` strokes, attributes only (CSP), decorative unless labelled |
| `src/api.ts` | Fetch client: anti-forgery header on state changes, ETags, error bodies |
| `src/document.ts` | Document model: immutable v1.0 JSON, client keys (`WeakMap`), index, path-copying edits, structural edits and their refusals, lossy-file detection, editability |
| `src/events.ts` | The tab's `EventSource`, subscriptions, de-duplication, stream re-creation, stream status |
| `src/store.ts` | A minimal external store with slice subscriptions |
| `src/drafts.ts` | Crash-recovery drafts in local storage (guarded; in memory for tests) |
| `src/workflowData.ts` | Workflow-level edits (metadata, arguments, variables, node ids, property values) and diagnostic locations |
| `src/context.ts` | The Studio context and slice-subscription hooks |
| `src/PropertyEditors.tsx` | Properties: node and workflow editors, one per property kind, raw JSON; the selected card's inline editors |
| `src/DataPanel.tsx` | Problems, Variables and Arguments tabs |
| `src/placement.ts` | Targets (list index or slot), placing, moving across containers, insert/paste targets, the clipboard format and id renaming |
| `src/dragdrop.ts` | Drag-and-drop by pointer hit-testing (no library), one indicator element and one cursor overlay |
| `src/studio.ts` | State and commands: connect, open, select, edit, insert, delete, move, undo/redo history, validate, save; runs (validate first, run dialog, recent runs, Stop, per-frame event batching); files (new, rename, delete, save as, unsaved prompt, conflicts, recovery) |
| `src/App.tsx` | Layout: command bar, Files panel, toolbox (Insert), the card designer (cards, card menu, collapse, zoom), properties, bottom tabs, Execution panel, run and file dialogs, status bar |
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
- `authoring.test.ts` (W4B): metadata, rows (free names, unknown fields kept, Out without required/default), JSON
  defaults, node ids, property values, assignment targets, diagnostic locations; undo of every new edit kind; live
  validation; going to a problem; read-only files.
- `AuthoringUi.test.tsx` (W4B): metadata via the breadcrumb with undo, breadcrumbs and id editing, literals and target
  suggestions, map entries, raw JSON of unknown activities, the variables and arguments tables, problems on rows with
  navigation, toolbox categories and search.
- `execution.test.ts` (W5): input arguments, the run dialog flow and `argumentText`, validation before run, not-started
  reasons, the lifecycle from server events, failures, timeouts and `MYRPA2004`, Stop (Cancelling…, 409, errors,
  refusals), node mapping (own workflow only, open file only), the event cap, concurrent runs on one stream, the
  recent-runs cap, per-frame batching, and stream re-creation with replay and gaps.
- `PanelsUi.test.tsx` (UX-2): namespaces, favorites (pin, remember, unknown types ignored), recent activities (order,
  insert from there), the bottom tab strip (automatic tabs, keyboard), splitters (keyboard, limits, remembered sizes,
  the grid), hiding and showing panels, malformed stored settings.
- `DesignerUi.test.tsx` (UX-3): card summaries and inline editors (editing on a card does not move the selection),
  card names, the card menu by keyboard, collapse with reveal of the selection, Expand all / Collapse all,
  ArrowLeft/ArrowRight, zoom state and limits.
- `ShellUi.test.tsx` (UX-1): every icon is a decorative SVG without style attributes; the command groups and their
  accessible names; disabled reasons; New workflow…; the status bar segments; the workflow title; the theme choice.
- `ExecutionUi.test.tsx` (W5): the run dialog (required, defaults, timeout, Cancel), the lifecycle and node states on
  the tree, Stop and Shift+F5, failure explanation and Select failed node, not-started labels, recent runs, the
  reconnecting and missing-events notices.
- `files.test.ts` (W6): new-workflow documents and path refusals, New (free name, create, open), the unsaved prompt
  (Cancel, Discard, Save, Save with a conflict), rename (open and other files, refusals), Save as, delete (open file
  closes; stale ETag refused), the four conflict choices, crash recovery (restore as an undoable step, stale drafts,
  identical drafts dropped, removal on save, write after a pause), and `--open` on connect.
- `FilesUi.test.tsx` (W6): the Files tree (folders, double-click, keyboard, F2, Delete), New with a refused name, the
  in-app unsaved prompt (never `window.confirm`), the conflict dialog, the recovery dialog.
- `corpus.test.ts` (W9): every diagnostic of the shared corpus (`tests/corpus`) is placed where the WPF
  `DraftValidator` places it (`tests/corpus/expected/locations.json`, written by the .NET `CorpusParityTests`), and
  every editable corpus file round-trips unchanged (ADR-0035).
- `App.test.tsx` also covers Ctrl+S, F5 and leaving the page with focus still in an edited field (W9).
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

- **Editing:** multi-selection; renaming a variable does not rewrite the expressions that use it; syntax highlighting
  and completion in expressions (ADR-0032 decided against CodeMirror for now).
- **Running:** persistent run history (only recent runs of this tab are kept); searching by execution or correlation
  id.
- **Files:** folder operations; moving files between projects; noticing outside changes before saving; recovery across
  browsers or machines.
- **Round-trip:** files with comments or trailing commas (they do not open); formatting preservation; number forms
  JavaScript would rewrite (they open read-only); duplicate JSON keys. See the parity document, §3.
- **Tooling:** OpenAPI-generated types (ADR-0028 decision 6).
- **Out of scope:** the recorder, agents and robots, remote execution, hosted mode and authentication beyond local
  mode, plugin management UI.
