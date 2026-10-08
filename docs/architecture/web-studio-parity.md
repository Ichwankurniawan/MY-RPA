# Web Studio parity with the WPF Studio (W9 exit review)

Status: W9 (2026-10-08). Decision: [ADR-0035](../adr/0035-web-studio-wpf-exit-review.md). This document is the evidence
for the [ADR-0021](../adr/0021-web-first-studio-and-wpf-removal.md) exit review. The WPF Studio may only be deleted (W10)
after a person has run the [manual test script](#4-manual-test-script-web-studio-prd-55) below and the owner has signed
off ([§5](#5-exit-review-sign-off)).

## 1. Corpus parity (diagnostic locations and round trip)

The shared corpus is `tests/corpus/*.json`: valid files with every value kind, maps, slots (also `case:` slots whose
names contain dots) and unknown fields at every level; files with every diagnostic group; a file with number literals
JavaScript would rewrite; a file with comments and trailing commas.

| Check | Where | Result (2026-10-08) |
|---|---|---|
| WPF `DraftValidator` locations recorded for every corpus file | `CorpusParityTests` (.NET, `tests/MyRPA.Studio.Core.Tests`) → `tests/corpus/expected/locations.json` | 11 files (10 open in WPF), 36 diagnostics, 26 codes; the golden file matches |
| Web Studio places every diagnostic on the same node (by tree position, so duplicate and invalid ids are exact), property, argument/variable row or the workflow | `web/studio/src/corpus.test.ts` | All 9 files both Studios open: every location equal |
| Every corpus file the Web Studio can edit is written back with the same values and key order, unknown fields included, also after an edit | `corpus.test.ts` (round trip) | Equal; the lossy-literal file opens read-only |

Codes covered: MYRPA1005, 1010, 1011, 1020, 1021, 1030–1033, 1040–1047, 1050–1052, 1060–1065. Not reachable through
the WPF Studio, so not compared: 1001/1002 (not JSON, not an object) and 1004 (wrong field type), which WPF refuses to
open, and 1003 (missing field), which WPF's draft model writes as an empty field (1021/1030). The Web Studio opens
1004 files and shows the server's diagnostic on the workflow.

W9 found and fixed one location difference: the Web Studio located node diagnostics by `nodeId` only, so an invalid or
missing id (MYRPA1030), an unknown field on a node (MYRPA1005), an unknown slot (MYRPA1051, WPF selects the slot's
activity), duplicate ids (MYRPA1031) and a slot named `case:1.5.properties` went to the wrong place. It now uses WPF's
rule: the longest node path that prefixes the diagnostic path with a remainder a node can have (ADR-0035).

## 2. Capability matrix

Every WPF Studio test group, and every capability it covers, with the Web Studio evidence. "Smoke" steps run against
the real server and engine in a browser (`npm run smoke`); the other names are Vitest suites in `web/studio/src`.

### Studio.Core `DraftJsonTests` (6 tests)

| WPF behavior | Web evidence |
|---|---|
| Save → open → save is stable text | `history.test.ts` "reproduces the edited structure from the saved file"; `corpus.test.ts` round trip |
| Literals, maps and unknown fields preserved | `document.test.ts` "preserve unknown fields everywhere"; `corpus.test.ts` (every level, also after an edit) |
| Comments preserved | **Difference:** the Web Studio cannot open such files (§3) |
| Semantically invalid workflows still open | `corpus.test.ts` (every `diag-*` file opens); `studio.test.ts` validation |
| The JSON path of every node | `nodeJsonPath` in `corpus.test.ts` and `authoring.test.ts` |

### Studio.Core `EditingTests` (20 tests)

| WPF behavior | Web evidence |
|---|---|
| Paths, find and replace in nested nodes | `structure.test.ts`, `document.test.ts` (path-copying edits) |
| Insert children and fill slots; unique readable ids; refused into an unknown activity | `structure.test.ts`, `placement.test.ts`, `ParityUi.test.tsx`; smoke W7-1 |
| Remove a subtree, never the root | `structure.test.ts`, `history.test.ts`; smoke 7 |
| Move into a slot and back; refused into itself or for the root | `placement.test.ts`, `ParityUi.test.tsx`; smoke W7-2 |
| Set, replace and remove properties | `document.test.ts`, `authoring.test.ts` |
| Argument and variable defaults are JSON | `authoring.test.ts`, `AuthoringUi.test.tsx` |
| Clipboard round trip with subtrees; paste renames colliding ids incl. descendants | `placement.test.ts`, `ParityUi.test.tsx`; smoke W7-3 |
| History: undo/redo, dirty tracking, at most 200 steps | `history.test.ts` |
| A valid draft is runnable | `studio.test.ts`, `execution.test.ts`; smoke 13-14 |
| Diagnostics mapped to nodes, properties, arguments and variables; dotted slot names exact | `corpus.test.ts` (§1), `authoring.test.ts` |

### Studio.Core `PendingEditTests` (10 tests) and the shell's focus tests

WPF commits a property when its field loses focus, so Save, Save as, Run and Close first apply a typed value. In the
Web Studio every keystroke is already an edit of the document: there is no uncommitted value.

| WPF behavior | Web evidence |
|---|---|
| Ctrl+S / F5 / Close with focus still in an edited field use the typed value | `App.test.tsx` "Shortcuts and leaving with focus still in an edited field" (Ctrl+S, F5, `beforeunload`) |
| Several panes' pending values as one undo step | Not applicable: each field's typing is its own undo step (merged per field, `history.test.ts`) |
| A refused pending value cancels Save, Run and Close and keeps the typed text | **Difference:** invalid JSON or a duplicate map name is refused in place with its reason and the text stays in the field, but Save and Run use the last valid document (§3) |

### Studio.Core `StudioViewModelTests` (30 tests)

| WPF behavior | Web evidence |
|---|---|
| New workflow: untitled, valid, empty Sequence | `files.test.ts` "creates a valid workflow named after its file"; smoke W6-1 (the server finds no problems) |
| Drop from the toolbox inserts, selects and validates | `ParityUi.test.tsx` (drag from the toolbox); `authoring.test.ts` (live validation); smoke W7-2 |
| A property edit is one undoable step and clears the error | `history.test.ts`, `App.test.tsx`; smoke W4B-2 |
| Choice and target editors: allowed values, writable names | `AuthoringUi.test.tsx`, `authoring.test.ts` |
| Drop on a case zone asks for the case value; cancelled changes nothing | `ParityUi.test.tsx` "adds a named case": the value is typed in the zone, and Pick stays disabled until there is one |
| Move into its own subtree or onto itself refused | `placement.test.ts`, `ParityUi.test.tsx` |
| Copy/paste with a fresh id after the selection; cut selects the parent | `ParityUi.test.tsx`, `placement.test.ts`; smoke W7-3 |
| The root cannot be deleted | `structure.test.ts`, `history.test.ts` |
| Add appends to the selected container or after the selected activity | `placement.test.ts` "as the WPF Studio…", `structure.test.ts` |
| A refused edit is reported and changes nothing | `structure.test.ts`, `ParityUi.test.tsx` "says what is wrong with a paste" |
| Variables and arguments edited in grids; argument diagnostics on their rows | `AuthoringUi.test.tsx`; smoke W4B-1 |
| Go to error selects the node | `authoring.test.ts` "goes to where a problem belongs" |
| Workflow info edited in Properties | `AuthoringUi.test.tsx` (Workflow breadcrumb) |
| Save then open round trip and dirty state | `studio.test.ts`, `history.test.ts`; smoke 10-12, W4B-3 |
| Unreadable files reported | `document.test.ts` "refuses what it cannot parse" |
| Toolbox categories and filter | `AuthoringUi.test.tsx` |
| An unknown activity shown without drop zones and reported | `AuthoringUi.test.tsx` (raw JSON editor), `placement.test.ts` (refusals) |
| Run: outputs, logs, completed nodes; blank argument uses the default; prompt cancelled does not run; bad argument text reported; invalid workflow not started; failure highlights the node; Stop; timeout; previous run states cleared | `execution.test.ts`, `ExecutionUi.test.tsx`; server `argumentText` tests; smoke W5-1…W5-6 |

### WPF shell `StudioShellTests` (16 tests)

| WPF behavior | Web evidence |
|---|---|
| Architecture rules (no async void, no mutable statics, banned APIs, references) | The .NET server is covered by the solution's architecture tests; `WebStudioRulesTests` forbids drag-and-drop frameworks in the Studio |
| Command line: file, plugins, configuration; required plugins; bad plugin directories fail | Server tests (`--open`, `--plugin`, `--plugin-config`, plugin loading); `ExecutionUi.test.tsx` shows plugin load failures |
| The window shows nested blocks; selecting shows property editors; runs show output and run states | `App.test.tsx`; smoke 1-14 |
| Asks before closing with unsaved changes | `App.test.tsx` (`beforeunload`); `FilesUi.test.tsx` (in-app prompt when opening another file); smoke W6-3 |

### Cross-cutting (ADR-0021 criteria 10-12)

| Criterion | Evidence |
|---|---|
| Keyboard and accessibility | `npm run a11y`: axe-core in 9 states, 0 violations, and a keyboard-only authoring pass (ADR-0034); screen-reader smoke test in the manual script (§4, step 12) |
| Performance | `npm run perf` on the 3,001-node fixture; measured in CI with a tolerance (ADR-0034) |
| CI on Linux and Windows | `.github/workflows/ci.yml` jobs `web-studio` and `web-studio-e2e` (ubuntu and windows matrix). **They have not run on GitHub yet:** nothing has been pushed (§5) |

## 3. Known differences (for the owner's review)

| Difference | Effect | Proposed disposition |
|---|---|---|
| Files with JSON comments or trailing commas (accepted by the engine) cannot be opened in the Web Studio | The Studio says why; the CLI and the server still run them. WPF opened them but dropped the comments on save | Accept for Phase 5 (no samples use them); a comment-preserving reader is later work |
| Number literals JavaScript would rewrite (`1.0`, `1e3`, integers beyond 2^53) and objects with integer-like keys | The file opens read-only, with the reason | Accept (WPF rewrote them silently) |
| A refused value (invalid JSON, duplicate map name) stays in its field with the reason; Save and Run use the last valid document, they are not cancelled | The refused text is not saved; leaving the page warns only if the document itself has unsaved changes | Accept, or add a "fix the refused value first" guard before W10: owner's choice |
| A non-string `version` (MYRPA1004) | The Web Studio opens the file and shows the diagnostic; WPF refused to open it | Web is more capable |
| Case values are typed into the case zone instead of a prompt after the drop | Same outcome; no modal | Accept |
| The status line and the Execution panel show the latest run of the tab, whichever file it ran | Recent runs selects any run; each run names its file | Accept, or show only the open file's runs: owner's choice |

## 4. Manual test script (Web Studio, PRD 5.5)

Replaces the WPF script in [studio.md](studio.md) §8. Prepare: `npm ci && npm run build` in `web/studio`, then
`dotnet build MyRPA.sln -c Release`. Use a scratch copy of `samples/`.

1. **Start:** `dotnet run --project src/MyRPA.Server -c Release -- --project <scratch>`; open the printed start link.
   The Files panel lists the project.
2. **Create:** Files › New…, name `greeter.json`. It opens, valid. Select the **Workflow** breadcrumb and set Name to
   `Greeter`. The title gains `•`.
3. **Add activities:** drag *Assign* from the toolbox onto the drop zone; drag *If* below it; drag *Log* into the If's
   `then` zone. Activities with missing required properties are marked, and Problems lists them.
4. **Configure:** Arguments tab: add `who` (In, String), default `"World"`. Variables tab: add `message` (String).
   Assign: `to` = `message` (pick it from the suggestions), `value` = `'Hello, ' + who`. If: `condition` =
   `len(who) > 0`. Log: `message` = `message`. Problems is empty within a second of typing.
5. **Undo/redo:** Ctrl+Z (the Log message is removed), Ctrl+Y (it returns).
6. **Copy/paste/delete:** select Assign, Ctrl+C, Ctrl+V: a copy with a new id (for example `assign-2`) appears after
   it. Delete it with Del. Cut the Log (Ctrl+X), select the If's `else` zone, Ctrl+V; then Ctrl+Z twice.
7. **Save:** Ctrl+S. The `•` disappears. `dotnet run --project src/MyRPA.Cli -- validate <scratch>/greeter.json`
   reports it valid.
8. **Open:** open `hello-world.json` from Files, then `greeter.json` again. The workflow is back.
9. **Run:** F5, enter `Ada`, Start. Execution shows *Succeeded*, the log shows *Hello, Ada*, and every activity shows
   ✓.
10. **Failure:** add a *Throw* with `message` = `'boom'` at the end; F5. Execution shows *Failed* with the Throw named;
    *Select failed node* selects it.
11. **Close:** with an unsaved change, close the tab. The browser asks before leaving. Reopen the start link and open
    the file: the unsaved change is offered back (crash recovery).
12. **Screen reader** (NVDA or Narrator): the tree announces each activity, its level and its state; the toolbox
    buttons announce "Insert Log (Core.Log)"; the run status change is announced; dialogs announce their title and
    trap focus; everything above works without a mouse (Tab, arrows, Enter, F2, Del, Ctrl+X/C/V, F5, Shift+F5).

Automated coverage of the same steps (`npm run smoke`): 1 (start link, W6-8), 2 (W6-1, W4B-1), 3 (W7-1, W7-2),
4 (W4B-1, W4B-2), 5 (5-6, W4B-2), 6 (W7-3), 7 (10, W4B-3; the server's validation is the CLI's `WorkflowLoader`),
8 (11-12, W6-3), 9 (W5-1, W4B-3), 10 (W5-3), 11 (W6-3, W6-6); step 12's keyboard part by `npm run a11y`. The script
is still run by a person: it checks what tests cannot (layout, wording, screen-reader output).

`npm run manual` (W9) drives steps 1-11 exactly as written (drags from the toolbox, shortcuts, the CLI) and saves one
screenshot per step. It cannot observe the browser's own leave prompt (step 11) or a screen reader (step 12).

### Findings of the first run (2026-10-08)

| Step | Finding | Fixed |
|---|---|---|
| 3 | At 125 % zoom the Files buttons wrapped and file names showed through below the header | Compact one-row buttons (`690abb4`) |
| 3 | With the workflow selected (step 2), every toolbox entry was disabled, so not even a drag onto the empty list worked | Workflow selected = insert into the root; entries are only `aria-disabled` when the selection gives no place, so drags onto zones always work |
| 11 | Crash recovery missed edits made less than a second before the page was left (drafts were written after a pause only) | The draft is written at once on `beforeunload` / `pagehide` |
| — | The status line and Execution panel show the latest run of any file (a failed `demo-plugin.json` run while `greeter.json` was open) | Open: owner's choice (§3) |

## 5. Exit review sign-off

| # | Item | Status |
|---|---|---|
| 1 | Criteria 1-13 met with the evidence above; full local validation green (W9 report, 2026-10-08) | ✅ automated |
| 2 | CI green on Linux and Windows including smoke, accessibility and performance | ⬜ needs a push (not done without the owner) |
| 3 | A person ran §4 steps 1-12 (name, date, browser, screen reader, findings) | ⬜ |
| 4 | The owner accepted the known differences (§3) | ⬜ |
| 5 | The owner signed off the exit review; W10 may delete the WPF Studio | ⬜ |
