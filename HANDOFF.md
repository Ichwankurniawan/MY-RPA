# HANDOFF — MyRPA (written 2026-09-25, end of Web Studio W4A; updated after W4A approval)

For a fresh Claude Code session with no memory of earlier work. Read this, then `CLAUDE.md`, then the ADRs it names.
`CLAUDE.md` is the standing rulebook and wins where the two differ on rules. This file covers state, history, and
things the owner said that are not written down elsewhere.

**Where the work is:** `main` (= `origin/main`). W4A was approved by the owner and fast-forward merged. The next
slice is **W4B (Rich authoring)**, which must not start without the owner's authorization. The roadmap is
[docs/architecture/web-studio-roadmap.md](docs/architecture/web-studio-roadmap.md).

---

## 1. Project goal

MyRPA is a C#/.NET 10 robotic process automation platform, similar in spirit to UiPath or OpenRPA:
- a workflow engine that runs JSON workflows of activities;
- an Automation SDK with in-process plugins (a Playwright browser plugin exists);
- a CLI;
- a Studio (visual designer).

It is built phase by phase from `MyRPA-PRD.md` §9.

Since W0, an owner-approved **web-first track** (ADR-0021..0029) is replacing the WPF Studio with:
- a **browser Web Studio**: React + TypeScript + Vite in `web/studio`;
- a **control-plane server**: `src/MyRPA.Server`, ASP.NET Core, local mode now.

This lets later work (remote execution, agents/robots, the Phase 6 recorder, AI/MCP, orchestration) share one UI and
one control plane. The workflow JSON v1.0 format (`docs/architecture/workflow-format.md`) is the only persisted format.

## 2. Current state (all verified on 2026-09-25)

| Milestone | Commit | Content |
|---|---|---|
| Phase 0–1 | `0459f5a` | OpenRPA research, solution skeleton, architecture tests |
| Phases 2–4 | `dc82caf` | Engine (`MyRPA.Workflow`, `MyRPA.Runtime`, `MyRPA.Activities`), Automation SDK and plugin host (`MyRPA.Sdk`, `MyRPA.Plugins`), Playwright browser plugin (`plugins/MyRPA.Browser.Playwright`), CLI |
| Phase 5 | `7a7b853` | WPF Studio (`MyRPA.Studio`, `MyRPA.Studio.Core`), now **frozen** (see §3) |
| W0 | `6dff95c` | ADR-0021..0025 and a measured React/SSE spike in `spikes/w0-web-studio` (throwaway; uses dnd-kit; never reuse) |
| W1 | `84cc822` | ADR-0023 per-run execution observer in the engine (`WorkflowRunRequest.Observer`); new `src/MyRPA.Contracts` (wire DTOs, BCL only) and `src/MyRPA.Execution.Hosting` (`ExecutionHost`, `ExecutionHandle.ReadEventsAsync(after)`, bounded replay with `stream.gap`, log routing) |
| W2 | `ba857ba` | `src/MyRPA.Server` local mode (loopback, start-token → cookie, Host/Origin/anti-forgery checks, CSP; projects and files with ETags; catalog; validate; runs; one multiplexed SSE stream per tab); ADR-0026 (MYRPA1040 path now `…properties.<name>`); ADR-0027 (InvokeWorkflow confinement unchanged) |
| W3 | `2d15bbc` (**main**) | `web/studio` first slice: open, ARIA tree, select, edit string properties, validate, save (If-Match), run with live SSE events/logs/status. Server `--web <dir>` serves the built Studio. ADR-0028. Architecture rule against drag-and-drop libraries. CI job `web-studio` |
| W4A | `62f2d05` (approved, on **main**) | Insert, delete, move up/down; snapshot undo/redo (200 steps, typing merged); keyboard commands; ADR-0029; browser smoke test extended; `npm run perf` |
| Roadmap | on **main** | Owner-defined W-roadmap W4B..W10 with the ADR-0021 exit criteria mapped onto it (`docs/architecture/web-studio-roadmap.md`) |

**Last full validation (W4A, the exact code of `62f2d05`; later commits are docs only):**
- `dotnet format MyRPA.sln --verify-no-changes`: clean.
- `dotnet build MyRPA.sln -c Release --no-incremental`: 0 warnings.
- `dotnet test --solution MyRPA.sln -c Release --no-build`: 955/955.
- `web/studio`, after `npm ci`:
  - `npm run typecheck`: clean;
  - `npm test`: 74/74 in 6 files;
  - `npm run build`: passes, 220 kB JS (69 kB gzipped);
  - `npm run smoke`: passes all 14 steps;
  - `npm run perf`: within every ADR-0021 target.

**Web Studio behavior now (W3 + W4A):**
- **Session:** the server's one-time start link, then an `HttpOnly`, `SameSite=Strict` cookie.
- **Open:** project select → workflow select → Open.
- **Tree:** children, then named slots. Keys ↑ ↓ Home End select; Delete deletes; Alt+↑/↓ move.
- **Properties:** editable display name and string values of the Expression, Text (dropdown when `allowedValues`
  exist), AssignmentTarget and LocalName kinds. Maps, non-string literals and unknown activities are read-only.
- **Toolbox:** catalog with search; each entry is an "Insert X (type)" button.
- **Edit toolbar** (`role=toolbar`, name "Edit"): Undo, Redo, Move up, Move down, Delete. Disabled buttons carry
  their reason in `title`.
- **Shortcuts:** Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z everywhere, Ctrl+S save, F5 run.
- **Validate:** `POST /api/validate`. Problems list entries select the node; errors also show on the property.
- **Save:** PUT with `If-Match`; a 412 keeps the edits and reports the conflict.
- **Run:** an unsaved document is sent as a buffer. One `EventSource` per tab. Shows status, per-node badges,
  events, logs, outputs and the error.

**How to run it:**
```bash
export DOTNET_ROOT="$USERPROFILE/.dotnet" PATH="$USERPROFILE/.dotnet:$PATH"   # this workstation; see §6
dotnet build MyRPA.sln -c Release
cd web/studio && npm ci && npm run build && cd ../..
dotnet run --project src/MyRPA.Server -c Release -- --project samples --web web/studio/dist --port 5310
# open the printed "valid once" link; saving writes to that project folder (use a copy of samples/ to keep the repo clean)
```

## 3. Key decisions (and why)

Accepted ADRs are in `docs/adr/` (index: `docs/adr/README.md`). The ones that matter most now:
- **ADR-0021: web-first.**
  - The Web Studio is the only long-term Studio UI.
  - WPF (`src/MyRPA.Studio`, `src/MyRPA.Studio.Core`, `tests/MyRPA.Studio*.Tests`) is a **frozen, temporary
    behavioral reference**: no features, no refactoring, never a design constraint. It is deleted in one PR once all
    ADR-0021 exit criteria pass and the owner signs off.
  - The exit criteria list (ADR-0021 §"Exit criteria") is the real backlog.
  - Performance targets the owner **tightened** after W0 (p95): edit/typing ≤ 50 ms; undo/redo ≤ 50 ms; structural
    edit ≤ 100 ms; drag activation ≤ 100 ms and drag move ≤ 50 ms; open ≤ 1 s at 3,000 nodes.
  - The owner **added** two exit criteria: toolbox search/filter, and editing workflow metadata.
- **No shared .NET authoring layer.** Editing, undo and clipboard live in the browser. The WPF `DraftEdits`,
  `DocumentHistory` and `DraftClipboard` are reference only; never port or share their code.
- **dnd-kit is rejected** (W0 measured +40 ms per edit, doubled heap, broken keyboard drops, ARIA conflicts).
  - The owner extended this to **any** drag-and-drop, sortable, diagram or canvas library.
  - Enforced by `tests/MyRPA.Architecture.Tests/WebStudioRulesTests.cs` via
    `ArchitectureRules.RejectedWebPackagePrefixes`, which checks both `package.json` and `package-lock.json`.
  - Future drag-and-drop means plain pointer hit-testing plus keyboard commands.
- **ADR-0022: server and dependency direction.** Core, Workflow, Contracts ← Execution.Hosting; engine, Plugins,
  Contracts, Execution.Hosting ← Server.
  - The engine never references Contracts, Execution.Hosting or Server.
  - ASP.NET Core is allowed only in `MyRPA.Server`, checked on **compiled** references
    (`PlatformNeutralityTests.AspNetCore_IsAllowedOnlyInTheServer`), because the Web SDK adds it implicitly.
  - The server serves the built Studio (`--web`, added in W3).
- **ADR-0023: engine events.** Events come from the per-run observer; observer failures never change a run (warning
  3006, the observer is dropped). No span listeners.
- **ADR-0024: SSE.** One stream per browser tab, multiplexing runs.
  - The SSE `id` is a position vector (`0=12,1=5`, subscription index = last sequence).
  - `Last-Event-ID` resumes every run.
  - Gaps arrive as `stream.gap` events.
- **ADR-0025: local security.** The start link is **not** opened automatically, because `Process.Start` is a banned
  API.
- **ADR-0026: MYRPA1040.** The diagnostic now points at `…properties.<name>` (one frozen WPF test assertion was updated
  for it, no WPF source change).
- **ADR-0027: InvokeWorkflow confinement.** Sub-workflows stay confined to the **entry workflow's folder**.
  - A project-root resolver was proposed; the owner **deferred** it ("do not implement").
  - `/api/runs` listing, the plugin-failure HTTP test and similar extras were also deferred "until W3/frontend need
    them"; none were needed so far.
- **ADR-0028 (W3).** The document model is the v1.0 JSON itself: immutable, stable client keys in a `WeakMap`, never
  saved.
  - Files JavaScript would rewrite open **read-only** instead of being silently changed: number forms like `1.0`,
    `1e3` or big integers, and objects with integer-like keys.
  - Wire types are **hand-written** (`web/studio/src/types.ts`), although ADR-0022 said they would be generated from
    OpenAPI. The owner confirmed in the W4A brief: "Hand-written API types remain the current W3 decision."
  - The dev-only Vite proxy re-labels Origin only for the dev page itself.
- **ADR-0029 (W4A).**
  - Structural edits are pure functions with refusal reasons.
  - Insertion point as WPF: the end of a selected list, else after the selection.
  - New node = `{id, type}` only, id `<type-last-segment-lowercase>-N`.
  - Delete selects next sibling, else previous, else parent. The root cannot be deleted or moved; slot activities
    cannot move.
  - Snapshot undo stores `{document, selectedKey, label}`, 200 steps. Consecutive same-field edits merge; selecting,
    undo, redo, save or other edits end the group.
  - Dirty = `document !== saved` (object identity).
- **Reversal:** ADR-0018 assumed a web Studio would reuse the WPF view models. ADR-0021 reversed that.

## 4. In progress

**Nothing is half-implemented.** W4A is complete, approved and on `main`. No W4B work exists yet.

For orientation, W4A touched:
- `web/studio/src/document.ts`: `nodeAt`, `insertionPoint`, `createNode`, `insertNode`, `deleteRefusal`, `removeNode`,
  `selectionAfterDelete`, `moveRefusal`, `moveNode`.
- `web/studio/src/studio.ts`:
  - `HistoryEntry`, `StudioState.undo/redo`, `maxUndo`;
  - `insertRefusal`, `deleteRefusalOf`, `moveRefusalOf`;
  - `Studio.commit` (private, where merging happens via `mergeKey`), `insertActivity`, `deleteSelected`,
    `moveSelected`, `undo`, `redo`.
- `web/studio/src/App.tsx`: `EditBar`; `Toolbox` Insert buttons; `WorkflowTree` keyboard handling and the `refocus`
  ref; global shortcuts in `Shell`.
- Tests: `src/structure.test.ts`, `src/history.test.ts`, `src/StructureUi.test.tsx`.
- Browser scripts: `scripts/harness.mjs` (new shared runner `withStudio`), `scripts/smoke.mjs` (rewritten), and
  `scripts/perf.mjs` (new).
- Docs: `docs/adr/0029-*.md`, `docs/architecture/web-studio.md`, `CLAUDE.md` (phase line).

## 5. Next steps (in order)

1. **Wait for the owner's W4B authorization.** Do not start anything else, including Phase 6.
2. When authorized, W4B (Rich authoring) starts on a new branch (for example `w4b-rich-authoring`) from `main`.
   - The owner's brief sets the scope. The proposed contents are in `docs/architecture/web-studio-roadmap.md` §W4B:
     all six property-kind editors, CodeMirror expressions, the variables/arguments editor, the metadata editor,
     editable ids, and raw properties of unknown activities.
   - The roadmap lists the decisions the owner must make for each slice (for W4B: CodeMirror as a runtime dependency,
     and automatic debounced server validation).
3. The later slices, in the owner's order:
   - W5 Execution UX;
   - W6 Project/file management;
   - W7 Advanced authoring / parity (slots, cross-container moves, copy/paste, drag-and-drop);
   - W8 Performance + accessibility + hardening;
   - W9 WPF exit criteria + migration;
   - W10 WPF removal / Phase 5 completion.
   After W10 the PRD continues with Phases 6–12.
4. The layout issue from §6 is not assigned to a slice yet; raise it when a UI slice is authorized.

## 6. Known issues and gotchas

- **Toolchain on the old workstation:**
  - The .NET 10 SDK was user-local at `%USERPROFILE%\.dotnet` (not on PATH). Git Bash needed
    `export DOTNET_ROOT="$USERPROFILE/.dotnet" PATH="$USERPROFILE/.dotnet:$PATH"`. A new machine may differ; check
    `dotnet --version` (see `global.json`).
  - Node 22 / npm 10.
  - `gh` CLI was not installed and winget is blocked by policy, so the owner opens PRs in the browser.
- **Tests run on Microsoft.Testing.Platform (xUnit v3).** The syntax is
  `dotnet test --solution MyRPA.sln` or `dotnet test --project tests/X -- --filter-class "*Name"`. The classic
  `--filter` does not work.
- **Browser prerequisites.** Playwright Chromium **revision 1243** must be installed once:
  `pwsh plugins/MyRPA.Browser.Playwright/bin/Release/net10.0/playwright.ps1 install chromium`.
  - The .NET browser plugin tests need it.
  - `npm run smoke` / `npm run perf` also reuse it: `playwright-core` is pinned to **1.63.0** to match
    `Microsoft.Playwright` 1.63.0.
  - Never upgrade one without the other.
- **`npm run smoke` / `perf` need** the Release server built (`src/MyRPA.Server/bin/Release/net10.0/MyRPA.Server.dll`),
  `web/studio/dist` built, and `dotnet` on PATH.
  - They copy workflows into a temp project, so the repo is never edited.
  - Screenshots go to `web/studio/test-results/` (gitignored).
- **Windows file locks.** A running `MyRPA.Server` locks its Release DLLs. Stop it (for example
  `Stop-Process -Id <pid>`) before `dotnet build -c Release --no-incremental`, or the build fails.
- **Bash heredocs mangle backslashes and quotes** in this environment. This caused a real bug: the Vite proxy key
  `'^/\\?token='` lost a backslash, so the dev start link was never proxied. Write files with the Write/Edit tools, or
  put Python edit scripts in a file and run them. Do not inline them in bash.
- **The start link is one-time and expires 10 minutes after the server starts.** Opening it anywhere, including with
  curl, consumes it. To test without consuming it, hit `/` or `/api/info` (a 401 without a session is expected).
- **CSP is `default-src 'self'`.** The Vite build must keep having no inline script or style (the smoke test checks for
  CSP violations). React `style={}` props are fine (CSSOM); `<style>` tags or `style="..."` in HTML are not.
- **Flaky .NET test:** `Plugin_UnloadsAfterItsBrowsersAreClosed` in `tests/MyRPA.Browser.Playwright.Tests` (a GC-based
  unload check). It failed during the W1 merge, including on unmodified `main`, and passed since. Pre-existing and left
  untouched; the owner has not decided what to do.
- **Web Studio edge cases (by design, documented):**
  - Files with JSON comments or trailing commas cannot be opened (`JSON.parse`), although the engine accepts them.
  - Saving re-indents with 2 spaces.
  - Deleting the activity in a required slot is allowed (validation reports it), but only Undo can refill the slot,
    because slot insertion does not exist yet.
  - Ctrl+Z in a text field undoes the **document** (intentional; typing is document history).
- **Layout** (seen in the owner's screenshot at a narrow window or high zoom):
  - the toolbar wraps;
  - node ids wrap inside tree rows;
  - the Run panel (grid row fixed at 240 px in `web/studio/src/style.css`) clips the event list.
  - Cosmetic; not fixed; mention it if polishing is authorized.
- **Performance headroom:** keystroke p95 was 34–46 ms against the 50 ms target on 3,000 nodes. Most of it is React
  rendering: `indexDocument` rebuilds per new version and every `TreeNode` runs 4 selectors. Watch it when adding
  per-node UI.
- **Line-ending warnings** (`LF will be replaced by CRLF`) on `git add` are harmless.
- **Verify git state before trusting premises.** Before W4A, the owner believed W3 was already merged; it was not. I
  asked, and they chose "commit and merge W3, then W4A". Always check `git status` / `git log` first.

## 7. Context not obvious from the code

**Working agreement with the owner** (Ichwan Kurniawan, GitHub `Ichwankurniawan/MY-RPA`):
- Never start the next phase or slice without explicit authorization.
- Work strictly inside the stated scope. The owner's briefs list "do NOT" items; honor them literally.
- End every step with the requested report, then **stop**.
- Never claim a pass that was not run. Report exact counts.
- Commit and push **only when asked**.
- Ask (AskUserQuestion) only for genuine blockers or state conflicts. Otherwise proceed with sensible defaults and
  state them.
- For output written for someone else, name the audience in one line in the reply.

**Commit discipline used for W0–W3:**
1. Confirm the scope in git (only the phase's files; no WPF changes; next phase not started; `samples/` unchanged).
2. Run the clean Release validation: `dotnet format --verify-no-changes`, `dotnet build -c Release --no-incremental`
   with 0 warnings, and all tests.
3. For web work, also run `npm ci`, typecheck, test, build and smoke.
4. Review the diff.
5. Commit on the phase branch with a message `Add W<n>: <summary>` and a bullet body, ending with
   `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
6. `git merge --ff-only` into `main` (the history is linear), then `git push origin main`.
7. Report the commit hash, merge result, `main` HEAD, results, working tree, and confirmations.

**Branch names used:** `w2-server`, `w3-web-studio`, `w4a-structural-editing` (the owner's briefs sometimes guess
names like `w2-server-control-plane`; use the actual one).

**Standing constraints repeated in every brief:**
- Do not modify WPF.
- Do not start the recorder, agents/robots, remote execution or scheduling.
- Do not redesign workflow JSON v1.0.
- No shared .NET authoring model.
- Do not weaken the Contracts / Execution.Hosting boundaries or any security control.
- No unrelated refactoring.
- The server should not change just to make the frontend easier. If an endpoint is genuinely missing: prove it, make
  the smallest additive change, document it, and add an API test.

**Architecture tests** (`tests/MyRPA.Architecture.Tests`) encode the rules:
- Adding a `src` project needs an `ArchitectureRules.SourceProjects` entry, a row in `docs/architecture/overview.md`,
  and an ADR if the dependency direction changes.
- Every new rule needs a known-bad self-test.
- When adding a rule, mutation-check it (plant a violation, see it fail, restore).

**Other conventions:**
- **Frontend dependency policy:** runtime is React and React DOM only. No Redux or other state library, no UI
  framework, no Monaco/CodeMirror unless a slice genuinely needs it, and no diagram/canvas/DnD libraries. The state
  store is the ~30-line `web/studio/src/store.ts` (`useSyncExternalStore`).
- **Tree performance pattern:** `TreeNode` is memoized on node-object identity and subscribes to its own
  selection/error/status slices. Keep that pattern; the W0 spike showed index-based keys cost 80+ ms per insert.
- **`spikes/`** is throwaway measurement code: not in the solution, never referenced.
- **Memory** is kept at `~/.claude/projects/<project>/memory/`, per machine; this file replaces it on the new machine.
