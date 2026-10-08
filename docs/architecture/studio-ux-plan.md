# Studio UX slice — plan (proposed)

Status: **approved** by the owner (2026-10-08), with the proposed answers below. In progress: UX-1. Follows Phase 5 (the Web Studio, ADR-0021 to ADR-0036).
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

## Owner decisions (2026-10-08)
1. The three sub-slices are approved (the minimap stays optional).
2. Icons: our own SVG set, drawn for MyRPA (no dependency).
3. The activity panel is grouped by type namespace (no server change).
4. Order: UX-1 → UX-2 → UX-3, then graph workflows (ADR-0037: flowchart, then state machine), then Phase 6.
