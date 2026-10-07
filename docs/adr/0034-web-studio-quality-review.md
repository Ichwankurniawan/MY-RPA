# ADR-0034: Web Studio quality — performance, accessibility, security and dependency review (W8)

- Status: Accepted for W8
- Date: 2026-10-08
- Phase: Web Studio W8
- Builds on: [ADR-0021](0021-web-first-studio-and-wpf-removal.md) (exit criteria 10–12), [ADR-0025](0025-local-mode-security.md),
  [ADR-0030](0030-web-studio-execution-ux.md)–[ADR-0033](0033-web-studio-parity-authoring.md)

## Context
W4B–W7 completed the features. W8 adds no features: it measures, reviews and hardens, and makes the checks repeatable
in CI.

## Decision
1. **Performance is measured, in CI too.** `npm run perf` (3,001-node fixture, production build, headless Chromium,
   real server) measures open, keystroke, undo, redo, insert, delete, move, drag activation and drag movement against
   the ADR-0021 targets (p95, each sample timed in the page to the next painted frame). CI runs it on Linux and Windows
   with `MYRPA_PERF_TOLERANCE=2`, because shared runners are slower and noisier; the factor is printed with the results.
   Typing during an event-heavy run is reported, not asserted (it is not an ADR-0021 target).
2. **Two measured optimizations.**
   - Per-node state (selection, error, run state, picked zone) is one store subscription returning a primitive string
     instead of four: with 3,000 nodes this removes 9,000 listener calls per keystroke. Keystroke script time p50 went
     from about 28 ms to about 21 ms; keystroke p95 from 52–55 ms to 38–42 ms on quiet runs.
   - Streamed events are applied at most 100 per frame (was 250): typing during an 8,700-event burst went from p95
     111–156 ms to 86–101 ms and long tasks from 34 to 5–9, while the whole burst shows in about 6.5 s instead of 3.5 s.
3. **Accessibility is checked automatically and by hand.** `npm run a11y` runs axe-core (a development dependency only,
   evaluated through the DevTools protocol so the CSP is unchanged) on the designer, workflow details, Variables,
   Arguments, the run dialog, the Execution panel after a run, a file dialog, and twice in the dark theme, and fails on
   any serious or critical violation. It also authors by keyboard only (arrows, Enter on the toolbox and on a slot zone,
   typing, Ctrl+X, Ctrl+V, Ctrl+Z, Ctrl+S). Fixed: toolbox button names did not contain their visible text (the
   description now sits outside the button and is referenced by `aria-describedby`); the Succeeded badge's contrast;
   the designer is the `main` landmark. Result: 0 violations of any impact in all 9 states.
4. **Security review** of everything added since W2 (findings and dispositions below); local-mode security (ADR-0025)
   is unchanged.
5. **Dependency review.** Runtime dependencies are React and React DOM only. Development dependencies: TypeScript, Vite,
   Vitest, Testing Library, jsdom, `playwright-core` (pinned to the .NET plugin's Playwright version), and axe-core
   (added in W8, test-only). No drag-and-drop, state-management, editor or UI framework was added (the architecture
   test still forbids DnD libraries). `npm audit`: the high-severity `source-map-js` advisory (a build-time,
   transitive dependency) is fixed by `npm audit fix`; 0 vulnerabilities remain.

### Security review (2026-10-08)

| Area | Finding | Disposition |
|---|---|---|
| Session, Host, Origin, anti-forgery (ADR-0025) | Every new endpoint (`argumentText` on runs, `move`, `/api/info.open`) goes through the same middleware | Tests: 401 without a session, 403 without the header, for runs and move |
| Paths | `move` resolves both paths through `ProjectStore.TryResolve` (project-relative, no `..`, no hidden segments, `.json` only, no reparse points) and never overwrites | Tests: escapes, backslashes, hidden segments, wrong types, malformed bodies → 400 |
| `--open` | A command-line path, must be an existing `.json` inside a project (or its folder becomes the project) | Tests; only the server's owner can pass it |
| Bundled Studio | Served by the same static-file rules as `--web` (hidden and unknown types excluded) | Unchanged tests (W3) |
| Request size | Kestrel's limit (5 MB) applies to every body | Unchanged |
| SSE | Streams belong to their session; a client that takes nothing for 10 s is dropped (bounded memory) | Tests (W2, W5 fix) |
| Run inputs | Argument text is parsed by the engine's `TryParseText`; bad text is a 400 that names the argument, never echoes other values | Tests (W5) |
| Error leakage | Unhandled errors return no details (Production environment); run errors carry the engine's message, code and node, no stack | Reviewed |
| Browser | No `innerHTML`, `eval` or URL navigation from data; React escapes text; selectors are built from internal keys only; pasted clipboard text is parsed as JSON and validated by the server like any edit; local drafts stay in the browser | Reviewed |
| Execution events | Never carry variable values or arguments (ADR-0023); logs carry what the workflow logged | Unchanged |

## Consequences
- ADR-0021 criteria 10 (keyboard and accessibility: every operation without a mouse, focus-safe shortcuts, ARIA tree,
  announced status, automated check) and 11 (performance targets, measured in CI) are met; criterion 12's CI is
  defined (unit tests on Linux and Windows; smoke, accessibility and performance end to end on both). A screen-reader
  pass by a person remains part of the W9 manual script.
- Local measurements on this workstation vary by about ±7 ms between runs of the same build; a single run above a
  target is repeated before it is treated as a regression.
