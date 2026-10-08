# Studio UX slice — plan (proposed)

Status: **approved** by the owner (2026-10-08), with the proposed answers below. UX-1 and UX-2 done (2026-10-08); next: UX-3. Follows Phase 5 (the Web Studio, ADR-0021 to ADR-0036).
Companion: [ADR-0037](../adr/0037-flowchart-and-state-machine-workflows.md) (flowchart and state machine, proposed).

## Goal
Make the Studio feel like a complete desktop-class designer: a grouped command bar, a richer activity panel, one
bottom panel with tabs, a zoomable designer, resizable panels and a consistent visual theme, while keeping every
Phase 5 guarantee: one workflow format, server-side validation and execution, CSP, accessibility (axe 0 violations,
keyboard-only operation) and the performance budgets.

## Rules (from the Phase 5 brief, unchanged)
- **Our own design.** No UiPath (or other vendor) icons, assets, names, layout copies, colours or wording. Common
  designer conventions (command groups, a toolbox, a properties pane, bottom tabs) are fine; their look is ours.
- No workflow-format change (the graph canvas is ADR-0037, a separate decision).
- No new runtime dependency without the owner's approval (icons are drawn as our own inline SVG components).
- No placeholders for later phases (no Recorder, Publish, AI or Orchestrator buttons until those phases exist).

## Scope, in three sub-slices

### UX-1 — Shell, command bar, theme
| Item | What | Done when |
|---|---|---|
| Command bar | Grouped commands with icons and labels: **File** (New, Save, Save as), **Edit** (Undo, Redo, Cut, Copy, Paste, Delete), **Run** (Validate, Run, Stop), **View** (panels, zoom). Each command shows its shortcut in the tooltip; disabled commands give their reason | Every command reachable by mouse, keyboard and its accessible name; axe 0 |
| Icon set | About 25 monoline SVG icons drawn for MyRPA, as React components (`currentColor`, no inline styles) | Contrast ≥ 3:1 for icons and ≥ 4.5:1 for text, light and dark |
| Theme | Refreshed design tokens: palette, type scale, spacing, radius, focus ring, light/dark | Tokens only in `style.css` `:root`; no inline styles (CSP) |
| Status bar | Connection, file, dirty state, last validation, current run | Announced through the existing live region |
| Title area | Workflow name and description shown above the designer (edited through the Workflow breadcrumb, as now) | — |

### UX-2 — Panels and activity panel
| Item | What | Done when |
|---|---|---|
| Activity panel | **Favorites** (pin/unpin per activity), **Recent** (last 10 inserted), then categories; grouped by type namespace (`Core`, `Browser`, plugin namespaces); search as now | Favorites and Recent survive a reload (per browser, `localStorage`, best effort) |
| Bottom panel | One tab strip: **Problems (n)**, **Variables (n)**, **Arguments (n)**, **Execution**, **Output/Log**, with counts and keyboard tab navigation | Counts update live; the Problems tab draws attention when validation fails |
| Resizable panes | Drag splitters between toolbox / designer / properties and designer / bottom panel; collapse side panels; sizes remembered per browser | Splitters work by keyboard (arrow keys) and are announced (`role="separator"`) |
| Right rail | A narrow icon rail that switches the right panel (Properties now, more later) | — |

### UX-3 — Designer
| Item | What | Done when |
|---|---|---|
| Zoom | Zoom in/out/reset (Ctrl+= / Ctrl+- / Ctrl+0), fit to window, zoom indicator | Applied through the CSSOM (`element.style.transform`), CSP-safe; drag-and-drop hit-testing correct at every zoom |
| Collapse/expand | Collapse a container card; "collapse all / expand all" | Selection and keyboard navigation handle collapsed content correctly |
| Annotations | Show a node's `displayName` prominently; optional workflow description header | No format change (existing fields) |
| Minimap (optional) | Overview of long workflows for quick scrolling | Owner's choice |

## Stored per browser (`localStorage`, best effort, never required)
| Key | Value |
|---|---|
| `myrpa.ui.favorites` | JSON array of activity type names, e.g. `["Core.Log","Browser.Click"]` |
| `myrpa.ui.recent` | JSON array of up to 10 activity type names, most recent first |
| `myrpa.ui.panes` | JSON object of pane sizes in pixels and collapsed flags, e.g. `{"toolbox":240,"properties":340,"bottom":280}` |

## Quality gates (each sub-slice)
- Vitest for every new behavior; `npm run smoke`, `npm run manual`, `npm run a11y` (new states: command bar, bottom
  tabs, splitters, zoomed designer) and `npm run perf` (keystroke p95 ≤ 50 ms; drag within budget) pass; CI green.
- No server change is required (grouping uses the type namespace). Optional: a `source` field in the catalog to group
  by plugin name (ADR-0020 catalog minor version), only if the owner prefers plugin names to namespaces.

## Order
UX-1 → UX-2 → UX-3, each its own commits and report, like W4B–W8. UX-1 and UX-2 are mostly layout and styling; UX-3
touches drag-and-drop geometry and needs the most testing. ADR-0037's canvas builds on UX-3's zoom and pane work, so
UX goes first if both are approved.

## UX-1 result (2026-10-08)
- Delivered: the command bar (File, Edit, Run, View), 34 MyRPA icons and the favicon, the teal theme with a View ›
  Theme choice (System, Light, Dark), the workflow title, the segmented status bar. Edit commands moved from the
  designer into the command bar (same accessible names, so every test and the keyboard flow are unchanged).
- **Performance lesson 1:** the status bar's text changes on every edit; as an auto-height grid row it made the page grid
  lay out the 3,000-node designer again (keystroke p95 58–60 ms, insert 65–68 ms). It is now one fixed-height line with
  `contain: strict`; editing p95 returned to the earlier level (keystroke 49.6, undo 42.4, insert 53.7, drag
  activation 97.2 ms). Rule: anything whose text changes per edit must not resize a grid track.
- **Performance lesson 2:** the drag-movement measurement depended on layout: it dragged the root and containers (whose
  moves are refused) and hovered cards at their centre, so the share of refused moves (each a full-page style
  recalculation) changed with the page geometry. `perf.mjs` now drags leaf activities onto the gap above other leaves,
  never next to themselves: with that, the median drop-preview move is the same before and after UX-1 (15.6 ms; traced
  with Chrome invalidation tracking). Drag-movement p95 under the load on this workstation is 59–63 ms for both the
  earlier build and UX-1; CI measures it with its documented tolerance.
- Validation: Vitest 209/209, typecheck, smoke, manual (steps 1–11), accessibility (0 serious or critical), perf as
  above.

## UX-2 result (2026-10-08)
- Delivered: Favorites (star toggle) and Recent (last 10) at the top of the activity panel, then namespaces (Built-in
  first) and categories; one bottom tab strip (Problems, Variables, Arguments, Execution) whose tab is Studio state: a
  run shows Execution, Validate or a run refused by validation shows Problems, background validation never switches;
  splitters (drag, arrow keys, Home/End, announced as separators with their size) and View toggles for the activities,
  properties and bottom panels; all remembered per browser (`preferences.ts`).
- **Deferred: the right rail.** With Properties as the only right-hand panel it would be a placeholder (not allowed);
  it comes with the first second panel.
- Accessibility: the splitters first failed axe (a focusable separator needs `aria-valuenow`; they were outside every
  landmark). They now always state their size and sit in a "Panel sizes" landmark (`display: contents`, so they stay
  grid items): 0 violations of any impact in all 9 states again.
- **Performance lesson 3:** toggling `drop-refused` on the root re-matched `.dragging.drop-refused *` on all ~18,000
  elements. The drag cursor is inherited, so only the elements that set their own cursor (cards, buttons, files,
  splitters) are overridden now. Drag movement p95 went from 51–52 ms to 46.7 / 47.3 ms (p50 14–15 ms), drag
  activation p95 74 ms, keystroke p95 39–43 ms, undo 37–40 ms: every target met in two consecutive runs.
- Validation: Vitest 218/218, typecheck, smoke (the error case now checks Problems, the toolbar status and the
  Execution tab), manual (steps 1–11), accessibility (0 violations), perf as above.

## Owner decisions (2026-10-08)
1. The three sub-slices are approved (the minimap stays optional).
2. Icons: our own SVG set, drawn for MyRPA (no dependency).
3. The activity panel is grouped by type namespace (no server change).
4. Order: UX-1 → UX-2 → UX-3, then graph workflows (ADR-0037: flowchart, then state machine), then Phase 6.
