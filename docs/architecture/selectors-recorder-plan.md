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

## Rules (unchanged)
- No new runtime dependencies beyond the plugin's Playwright; the Studio keeps its CSP and libraries.
- Workflow runs never run JavaScript in pages (ADR-0017); only recording sessions use the recorder script (ADR-0039).
- Recorded steps are suggestions; inserting them is an ordinary, undoable edit.

## Out of scope
Selector healing, an element picker for existing activities, Firefox/WebKit, Windows/desktop selectors and Automation
ID (Phase 7), recording in a remote browser.
