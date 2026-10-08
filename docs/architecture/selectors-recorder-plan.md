# Phase 6 — Selectors & Recorder: plan

Status: **approved** (owner, 2026-10-08: "okay, proceed to phase 6"; decisions: a recorder-only script, a new contract
project, browser selector strategies now and Automation ID in Phase 7). Decisions: [ADR-0038](../adr/0038-browser-selectors.md)
(selectors), [ADR-0039](../adr/0039-browser-recorder.md) (recorder). PRD §9, Phase 6.

## Definition of done (PRD §6.6)
A user can record a simple website interaction and insert the generated activities into the Studio.

## Slices (each with its own commits, report and CI)

| Slice | What | Done when |
|---|---|---|
| S-1 Selector format | `text="…"` (exact), `label=`, `attr=name=value`, `testid=`; resolution of the new strategies; AutomationId refused (Phase 7) | Parser/format/resolution tests against real pages; Phase 4 selectors and samples unchanged; `browser-automation.md` final syntax |
| S-2 Contracts and recorder backend | `MyRPA.Browser.Contracts`; the extension-contract rule in the plugin loader; the plugin's recorder: headed session, `recorder.js` (events, selector candidates per ADR-0038 §3), binding with validation, uniqueness re-checked by locators, steps for click / type / navigate / select / upload / download, password typing never sent | Plugin tests drive a recording session (headless, test flag) with Playwright as the user on local pages and get the expected steps and selectors; architecture tests for the new project and the script exception |
| S-3 Server | `POST /api/recordings`, steps on the event stream, `DELETE` to stop; 503 without the plugin; security checks | Server tests (with the plugin loaded) start a recording, receive steps, stop |
| S-4 Studio | Record dialog (start URL), Recording panel (live steps, choose an alternative selector, edit a value, delete), Stop, Insert (Open, steps, Close, `password` argument) as one undo step | Vitest; smoke: record against a local test page by driving the visible browser, insert, save, run headless: Succeeded; a11y with the panel; the DoD demo |

## S-1 result (2026-10-08)
- Delivered: `text="…"` (exact), `label=` (Accessibility), `attr=name=value` and `testid=` (Attributes), resolved through
  Playwright locators (`GetByText` exact, `GetByLabel` exact, a CSS attribute selector with an escaped value);
  `automationid=` refused with a Phase 7 message; Phase 4 syntax unchanged (`browser-automation.md` documents the
  final format).
- Validation: browser plugin tests 73/73 against real Chromium (new: each new form finds its element, exact text does
  not match part of a text, malformed `attr=`/`label=` and `automationid=` fail with `InvalidSelector`).

## S-2 result (2026-10-08)
- Delivered: `MyRPA.Browser.Contracts` (BCL only) shared with plugins; the extension-contract rule in the plugin
  registrar; the plugin's recorder (visible Chromium, `recorder.js` with selector candidates, a random binding name,
  validated messages, uniqueness checked by locators, typing coalesced, passwords never sent, navigation and
  downloads); `IRecordingTestDriver` for tests; a source-level architecture rule for page scripting (it did not exist
  before: `EvaluateAsync` had been banned by convention only), with a known-bad self-test.
- Found by the tests: a click on a link navigates before the browser can count matches, so the script now counts
  role/name, label and exact-text matches itself (used only then); Chrome already clicks a form's submit button on
  Enter, so the script's own Enter handler (a duplicate click) was removed.
- Validation: .NET 931/931 (Release; recorder tests 8/8 against real Chromium, headless), `dotnet format` clean.

## S-3 result (2026-10-08)
- Delivered: `GET/POST /api/recordings`, `GET/DELETE /api/recordings/{id}`; recording events (`recording.step`,
  `recording.ended`, wire contract `RecordingEventMessage` in `MyRPA.Contracts`) on the tab's existing event stream: a
  stream subscription now reads an `IStreamSource` (a run, unchanged, or a recording); one recording at a time,
  session-bound; 503 without the browser plugin; `--recorder-headless` (tests only).
- Validation: .NET 937/937 (Release; server recording tests 6/6 with the real browser plugin, headless; the 64 existing
  server tests unchanged), `dotnet format` clean.

## S-4 result (2026-10-08)
- Delivered: the Recorder tab (Record command, start address, live steps with selector choice, text editing and
  removal, Stop, Insert, Discard), recording events on the tab's one event stream, `POST /api/recordings/generate` (the
  browser plugin generates its own activities: the Studio has no browser-activity knowledge), insertion as one undo step
  with the `password` argument, `--recorder-debugging-port` (tests only).
- Found by the browser test: the Record command made the command bar wrap at 1400 px after a run (the run status
  widened the Run group), which shrank the designer; command groups are now small grids whose label never widens them,
  and the run status wraps to two lines.
- **Definition of done met:** the smoke test records a sign-in on a local website with real input in the recording
  browser, inserts it at the selection (Open, Type, Type password, Click, Close with semantic selectors; the password an
  argument, never stored), saves it and runs it headless: Succeeded.
- Validation: .NET 940/940 (Release, 0 warnings, `dotnet format` clean), Vitest 261/261, typecheck, smoke (new Phase 6
  DoD step), manual (steps 1–11), accessibility (0 violations, with the Recorder tab), perf (every target met).

## Phase 6 summary
Phase 6 is complete (S-1 to S-4, 2026-10-08): the final selector format (ADR-0038), the recorder (ADR-0039) from the
browser plugin through the server to the Studio, and the PRD §6.6 definition of done shown end to end. Phase 7 needs
the owner's explicit authorization.

## Rules (unchanged)
- No new runtime dependencies beyond the plugin's Playwright; the Studio keeps its CSP and libraries.
- Workflow runs never run JavaScript in pages (ADR-0017); only recording sessions use the recorder script (ADR-0039).
- Recorded steps are suggestions; inserting them is an ordinary, undoable edit.

## Out of scope
Selector healing, an element picker for existing activities, Firefox/WebKit, Windows/desktop selectors and Automation
ID (Phase 7), recording in a remote browser.
