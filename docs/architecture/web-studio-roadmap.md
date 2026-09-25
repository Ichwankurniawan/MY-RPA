# Web Studio roadmap (W-track, Phase 5 completion)

Status: owner-defined roadmap (2026-09-25). The slice names and order come from the owner. The per-slice contents
below map the open [ADR-0021](../adr/0021-web-first-studio-and-wpf-removal.md) exit criteria onto those slices, as a
**proposal** for later briefs. Each slice starts only with the owner's explicit authorization, and its brief sets the
final scope.

Phase 5 is complete when W10 removes the WPF Studio. After that, the PRD continues with Phases 6–12 (Phase 6 recorder
backend work may start in parallel once authorized; see ADR-0021 "Consequences").

| Work | Scope | Status |
|---|---|---|
| **W0** | Architecture, ADRs, performance spikes | ✅ Done (`6dff95c`) |
| **W1** | Execution events + hosting | ✅ Done (`84cc822`) |
| **W2** | Server / control plane | ✅ Done (`ba857ba`) |
| **W3** | First working Web Studio | ✅ Done (`2d15bbc`) |
| **W4A** | Structural editing | ✅ Done (`62f2d05`, approved 2026-09-25) |
| **W4B** | Rich authoring | 🔜 Next (needs authorization) |
| **W5** | Execution UX | ⬜ |
| **W6** | Project/file management | ⬜ |
| **W7** | Advanced authoring / parity | ⬜ |
| **W8** | Performance + accessibility + hardening | ⬜ |
| **W9** | WPF exit criteria + migration | ⬜ |
| **W10** | WPF removal / Phase 5 completion | ⬜ |

## Exit criteria status after W4A

Numbers refer to ADR-0021 "Exit criteria for deleting WPF".

| # | Criterion | Done (W3/W4A) | Open | Planned in |
|---|---|---|---|---|
| 1 | Authoring | Open, save; insert catalog activities by keyboard into lists; move within a list; delete; unique ids; display name; toolbox search | Create, save-as; insert into slots (incl. `case:`); nest/move across containers; drag-and-drop; editable id; raw properties of unknown activities; lossless round-trip corpus; metadata editor | W4B, W6, W7 |
| 2 | Property editing | String values of Expression/Text/AssignmentTarget/LocalName; required markers, descriptions; allowed-value lists; no "typed but not committed" state | Map editor; literal editors; assignment-target suggestions; live expression syntax feedback | W4B |
| 3 | Variables and arguments | — | Whole editor | W4B |
| 4 | Validation | Diagnostics on node and property; Problems list navigates; MYRPA1040 on the property | Argument/variable/workflow-level locations; validation ≤ 500 ms after typing stops (debounced); corpus parity with Phase 5 `DraftValidator` | W4B, W9 |
| 5 | Undo/redo | 200 steps; typing merged; every current edit kind; dirty state incl. undo to saved | New edit kinds must join the history as they are added | each slice |
| 6 | Copy/paste | — | Cut/copy/paste within and across documents with id renaming; keyboard paste without a permission prompt | W7 |
| 7 | Execution | Run; status, outputs; error with failing node; per-node states; unsaved buffer; stream re-creation | Typed argument input; Stop; timeout control; reconnect proven end to end | W5 |
| 8 | Logs, output, errors | Level and node id; bounded (1,000); run id; no internals | Clear; execution and correlation ids visible | W5 |
| 9 | Projects and files | Open project (server `--project`); file list; save conflict (412); `beforeunload` | File tree; create/rename/delete; in-app unsaved prompt (not `window.confirm`); crash recovery; command-line equivalents of the WPF startup options | W6 |
| 10 | Keyboard and accessibility | Tree and commands by keyboard; Ctrl+S/Z/Y, Del, F5; ARIA tree; announced status | Ctrl+X/C/V, Shift+F5; every operation without a mouse; automated accessibility check; screen-reader smoke test | W5, W7, W8 |
| 11 | Performance | Open, edit, undo/redo, insert/delete/move within targets on the flat 3,000-node fixture (`npm run perf`, local) | Nested fixture; drag activation/move; validation ≤ 500 ms; measured in CI | W4B, W7, W8 |
| 12 | Automated tests | 74 Vitest tests; server API/security/SSE tests; browser smoke test; Linux web job (unit tests, build) | TS coverage of every Studio.Core behavior group; corpus conformance against `WorkflowLoader`; e2e for PRD 5.5 and the manual script; CI green on Linux and Windows incl. smoke | W8, W9 |
| 13 | Former WPF-only capabilities | Title and dirty marker; status line | Plugin load failures shown in the Studio; visible (non-headless) browser runs in local mode; single-command local start | W5, W6 |
| 14 | Process | — | Docs; a human runs the manual script; owner signs off the exit review | W9 |

## Slices (proposed contents)

### W4B — Rich authoring
- Property editors for all six kinds: key/value maps (ExpressionMap, AssignmentTargetMap), number/boolean/null
  literals, assignment-target suggestions.
- CodeMirror 6 expression editing (approved by ADR-0021) with live syntax feedback. Proposed source of truth:
  debounced server validation (W0: 12–17 ms round trip), so `WorkflowLoader` rules are not duplicated in TypeScript.
- Variables and arguments editor: add, remove, rename; type and direction; required; defaults as JSON with inline
  errors.
- Workflow metadata editor (id, name, version, description); editable node ids; raw property editing for activities
  missing from the catalog.
- Validation locations for arguments, variables and the workflow; validation ≤ 500 ms after typing stops.
- **Decisions for the owner:** CodeMirror as the first new runtime dependency since W3; whether live validation calls
  the server automatically.

### W5 — Execution UX
- Run dialog with typed argument input; Stop (Shift+F5, `POST /api/runs/{id}/cancel`); timeout control (`timeoutMs`).
- Log panel: clear, execution and correlation ids; reconnect after a dropped stream proven end to end.
- Plugin load diagnostics (`/api/plugins`) shown in the Studio; visible (non-headless) browser runs in local mode.
- **Decision for the owner:** how headful browser runs are configured (plugin configuration vs. a run option).

### W6 — Project/file management
- File tree; create, rename, delete, save-as within a project (create = `PUT` with `If-None-Match: *`; delete exists).
- In-app unsaved-changes prompt; crash recovery (local, per browser); conflict resolution UX on 412.
- Command-line equivalents of the WPF startup options (for example opening a file); single-command local start.
- **Decisions for the owner:** rename as a new server endpoint vs. create + delete; how the Studio is packaged for a
  single-command start.

### W7 — Advanced authoring / parity
- Insert into slots (including named `case:` slots); move and nest across containers.
- Cut/copy/paste within and across documents with id renaming (WPF `DraftClipboard` behavior as reference).
- Drag-and-drop by plain pointer hit-testing with a tree-aware nearest-valid-drop rule (ADR-0021; no DnD library).
- Lossless round-trip on the shared corpus (including the forms opened read-only today).

### W8 — Performance + accessibility + hardening
- Performance measured in CI on the flat and nested 3,000-node fixtures, including drag and validation latency.
- Automated accessibility check with no serious or critical violations; screen-reader smoke test; every operation
  without a mouse.
- Security and robustness review of the Studio and server paths added since W2.
- **Decision for the owner:** the accessibility checker (a new development dependency).

### W9 — WPF exit criteria + migration
- Corpus parity with the Phase 5 `DraftValidator` locations for every MYRPA10xx code.
- TypeScript tests covering every Studio.Core behavior group; e2e covering the PRD 5.5 definition of done and the
  Studio manual test script; CI green on Linux and Windows.
- Migrate the docs (`studio.md` and the manual test script) to the Web Studio; a human runs the script; owner exit
  review.

### W10 — WPF removal / Phase 5 completion
- One PR deletes `MyRPA.Studio`, `MyRPA.Studio.Core` and their tests.
- Removes the `IsDesktopUi` exemption, the linked-detector test setup, `CommunityToolkit.Mvvm` and the CI step that
  builds and tests the Studio; WPF becomes forbidden everywhere (ADR-0021 "Consequences").
- Updates the PRD, overview and ADR index; Phase 5 is complete.
