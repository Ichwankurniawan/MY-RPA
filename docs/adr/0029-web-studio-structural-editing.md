# ADR-0029: Web Studio structural editing and undo/redo (W4A)

- Status: Accepted for W4A
- Date: 2026-09-25
- Phase: Web Studio W4A
- Builds on: [ADR-0021](0021-web-first-studio-and-wpf-removal.md) (frontend baseline: immutable document, stable keys,
  snapshot undo, typing merged into one step, no dnd-kit), [ADR-0028](0028-web-studio-first-slice.md) (W3 document model)

## Context
W3 could open, edit properties, validate, save and run. W4A adds structural editing: insert, delete, move up and down,
and undo/redo, with explicit commands and keyboard shortcuts. Drag-and-drop stays out (ADR-0021 plans pointer
hit-testing for it later). The WPF Studio (`DraftEdits`, `DocumentHistory`) is the behavioral reference; no code is
shared or ported, and the server is unchanged.

## Decision
1. **Edits are pure functions on the v1.0 JSON** (`document.ts`). Each has a query that returns the reason it is not
   possible, so commands are disabled and explained; a refused edit never changes the document.
   - **Insert** (`insertionPoint`, as the WPF Studio): at the end of the selected node's list when its activity holds
     children, otherwise right after the selected node in its parent's list. Nothing is inserted into slots yet.
   - **New nodes** are `{ id, type }` only. The id is the lower-case last segment of the type plus `-N`, the smallest
     free number (`log-1`), as in the WPF Studio. No property defaults are invented; required properties stay unset
     until the user fills them, and validation says so on the property (ADR-0026).
   - **Delete** removes the selected node and its subtree only. The root cannot be deleted. Emptying a slot removes only
     that slot entry (validation reports a required slot). The next sibling, else the previous one, else the parent is
     selected.
   - **Move up/down** swaps the node with its neighbour in the same list. Slot activities and cross-container moves are
     not supported yet. The moved node keeps its object, so its client key and the selection survive.
2. **Undo/redo by snapshots** (as ADR-0021 and the WPF `DocumentHistory`).
   - Each step stores the previous document version and its selection. Versions share every unchanged subtree, so a
     step costs only the copied path; undo and redo re-use the older version and its cached index.
   - At most 200 steps. A new edit clears the redo list. Opening a file starts a new history.
   - Consecutive edits of the same property (or display name) of the same node form one step. Selecting another node,
     undo, redo, save or any other edit ends the group.
   - Dirty means "not the object last opened or saved", so undoing back to the saved version is clean, and undoing
     past a save is dirty again.
   - Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z undo and redo the document everywhere, including in text fields: typing is part
     of the document history, so there is no separate field-level undo.
3. **Keyboard.** In the tree: ↑ ↓ Home End select, Delete deletes, Alt+↑ / Alt+↓ move; focus follows the selection
   after these commands. The toolbox entries are Insert buttons (Tab, Enter). Disabled commands carry their reason as
   a tooltip, and refused keyboard commands report it in the status bar.
4. **Validation stays on the server.** Structural edits mark the diagnostics as out of date; the user validates
   explicitly. No `WorkflowLoader` rule is duplicated in TypeScript.

## Consequences
- Measured on the W0 3,000-node fixture (`npm run perf`, four runs, production build, headless Chromium): undo p95
  30–34 ms, redo p95 30–33 ms, keystroke p95 34–46 ms, insert/delete/move p95 34–47 ms, open 165–194 ms. All are within
  the ADR-0021 targets. Undo and redo spend 1–2 ms in script (older versions and their indexes are re-used); the rest
  is React rendering and waiting for the next frame.
- The WPF insertion rule, id scheme and root refusals are followed; paste, clipboard and drag-and-drop remain open (ADR-0021 exit
  criteria 1 and 6).
- Deferred: inserting into empty slots, moving between containers and into or out of slots, cut/copy/paste,
  drag-and-drop, editing node ids, multi-selection.
