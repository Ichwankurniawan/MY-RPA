# ADR-0032: Web Studio rich authoring (W4B)

- Status: Accepted for W4B
- Date: 2026-10-08
- Phase: Web Studio W4B (after W5 and W6, on the owner's instruction)
- Builds on: [ADR-0021](0021-web-first-studio-and-wpf-removal.md) (frontend baseline, exit criteria 1–5),
  [ADR-0026](0026-missing-property-diagnostic-location.md) (property locations),
  [ADR-0028](0028-web-studio-first-slice.md) (document model), [ADR-0029](0029-web-studio-structural-editing.md)
  (undo/redo)

## Context
W3/W4A edited only string values of four property kinds; maps, literals, variables, arguments, workflow metadata, node
ids and unknown activities were read-only, and validation ran only on request. ADR-0021 exit criteria 1–5 require all of
them, live expression feedback, and diagnostics on rows and the workflow. The WPF Studio (`DraftEdits`,
`DraftValidator`) is the behavioral reference.

## Decision
1. **One document, more pure edits.** `workflowData.ts` adds pure functions on the v1.0 JSON next to `document.ts`:
   metadata (a blank description is removed), argument and variable rows (add with a free name — arguments and
   variables share one namespace —, update, remove; unknown fields kept), node ids (trimmed), and any property value.
   Making an argument Out removes `required` and `default`, so the editor never creates MYRPA1065 by itself. Every edit
   is one undo step; typing in one field merges (ADR-0029). There is no second model and no .NET authoring layer.
2. **An editor per property kind**, from the server's catalog (no frontend schema): Expression (text; a literal number,
   boolean or null is shown as JSON and stored as the equivalent expression text once edited), Text (a choice list
   when the catalog names allowed values), AssignmentTarget (suggesting variables and Out/InOut arguments),
   LocalName, and the two map kinds (entries in order; a blank or duplicate name is refused in place, not stored).
   Properties the catalog does not describe — all of an activity missing from the catalog — are raw JSON, stored only
   while valid, with add and remove.
3. **Variables and arguments** are tables in the lower panel (name, direction, type, required, default as JSON stored
   only while valid). The workflow's metadata (id, name, version, description) is edited in Properties when the
   workflow is selected (the first breadcrumb).
4. **Live validation on the server.** Once typing pauses for 300 ms the document is validated by `POST /api/validate`
   (W0 measured a 12–17 ms round trip), in the background: it never blocks typing and never writes to the status line.
   This is the "live expression syntax feedback": the WorkflowLoader's own diagnostics on the property, with no rules
   duplicated in TypeScript.
5. **No CodeMirror.** ADR-0021 allowed CodeMirror 6 for expressions; it is not used. The feedback comes from the
   server either way, expressions are one-line inputs, and CodeMirror would be the first runtime dependency besides
   React and inject style elements, which the `default-src 'self'` CSP forbids. Plain inputs keep the dependency policy
   and the CSP intact. If richer expression editing (highlighting, completion) is wanted later, it needs its own ADR.
6. **Diagnostics are located like the WPF `DraftValidator`:** `$.arguments[i]`/`$.variables[i]` → the row; a node id
   and `.properties.<name>` → the property (map entries included); the node otherwise; `$.<field>` → that metadata
   field. The Problems list goes to each (selecting the node, switching to and focusing the row, or selecting the
   workflow).
7. **Designer and toolbox.** Activity cards with container boundaries; empty lists and missing slots are named inside
   the card (not as tree items, to keep the ARIA tree exact); breadcrumbs (Workflow › … › selection). The toolbox
   groups the catalog by category (collapsible), searches names, types, categories and descriptions, and says when
   nothing matches. Light and dark themes follow the system setting.

## Consequences
- ADR-0021 criteria 1 (metadata, editable id, unknown activities' raw properties, toolbox search), 2 (all six kinds,
  allowed values, target suggestions, map editor, live feedback), 3 (variables and arguments) and 4 (diagnostics on
  block, property, row and workflow; validation never blocks typing) are covered. Inserting into slots, cross-container
  moves, copy/paste and drag-and-drop are W7.
- Renaming a variable or argument does not rewrite expressions that use it (neither did the WPF Studio); validation
  reports the unknown names (MYRPA1044).
- Measured (`npm run perf`, 2026-10-08): keystroke p95 49.8 ms, undo 44.9 ms, redo 41.9 ms, insert 53.8 ms, delete
  50.6 ms, move 65.7 ms, open 237 ms — the W4A targets hold with the new editors.
