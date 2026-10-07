# ADR-0033: Web Studio parity authoring — slots, moves, clipboard, drag-and-drop (W7)

- Status: Accepted for W7
- Date: 2026-10-08
- Phase: Web Studio W7
- Builds on: [ADR-0021](0021-web-first-studio-and-wpf-removal.md) (no DnD library; pointer hit-testing; criteria 1, 6,
  10, 11), [ADR-0029](0029-web-studio-structural-editing.md) (structural edits, undo), [ADR-0032](0032-web-studio-rich-authoring.md)

## Context
After W4A/W4B the Web Studio inserted only into lists and moved only within a list. The WPF Studio (`DraftEdits`,
`DraftClipboard`, drop zones) also inserts into slots — including named `case:` slots —, moves across containers,
copies and pastes with id renaming, and drags and drops. ADR-0021 rejected dnd-kit (re-renders, heap, keyboard drops,
ARIA conflicts) in favor of plain pointer hit-testing plus keyboard commands.

## Decision
1. **Targets are a list index or a named slot** (`placement.ts`, pure, like the WPF `InsertPosition`). A place is
   allowed when the parent's catalog activity has the list or the slot (a prefix slot such as `case:` accepts any longer
   name) and the slot is empty; otherwise the reason is returned. A move refuses the root and moves into the node's own
   subtree, treats a move to where the node already is as a no-op, and adjusts indices and paths for the removal (as
   `DraftEdits.Move`). The moved node keeps its object, so its client key and selection survive.
2. **Where an insert or paste goes** (as the WPF Studio): an empty slot or list the user picked in the designer;
   otherwise into the selected node's list (at the end), else its first empty fixed slot, else right after it in its
   parent's list. Empty slots and empty lists are buttons inside the card (pick for the next insert or paste; also
   drop targets); a Switch has a "+ case:" zone that takes the case value first.
3. **Clipboard in the WPF format** `{"myrpaNodes":"1.0","nodes":[…]}` (readable workflow JSON, never client keys).
   Ctrl+X/C/V use the browser's `cut`/`copy`/`paste` events, which carry clipboard text without a permission prompt;
   inside text fields they stay the field's own. The Cut/Copy buttons also write the system clipboard; Paste pastes what
   this tab copied last. Pasting renames every id already in use (descendants too) to `<type name>-N`; several nodes go
   into a list in order, a slot takes one. Pasting works across documents and tabs.
4. **Drag-and-drop by pointer hit-testing** (`dragdrop.ts`), installed once on the Studio root by event delegation.
   A press becomes a drag after 5 px. `elementFromPoint` finds a zone (inside) or a card in a list (its upper or lower
   half: before or after). The same refusal functions as every edit decide validity; a refused place shows the
   not-allowed cursor and changes nothing. While dragging nothing goes through React: one indicator element is moved
   through the CSSOM (allowed by the CSP). The drop is one command and one undo step; Esc cancels. Toolbox entries drag
   in the same way to insert. Keyboard equivalents: Cut/Paste, Move up/down, and the zones.

## Consequences
- ADR-0021 criteria 1 (insert into slots incl. `case:`, nest and move across containers, drag-and-drop and keyboard
  insertion), 6 (cut/copy/paste within and across documents, id renaming, keyboard paste without a prompt) and the
  drag part of 11 are covered. Measured (`npm run perf`, 3,001 nodes, real layout): drag activation p95 76.8 ms (target
  100), drag movement p95 42.9 ms (target 50).
- Not included: dragging onto a container card's middle to append to its list (use its empty-list zone or a gap), a
  multi-node selection, and renaming an existing `case:` slot (cut and paste into a new case instead).
