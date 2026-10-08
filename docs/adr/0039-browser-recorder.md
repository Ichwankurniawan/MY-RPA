# ADR-0039: Browser recorder — recording sessions, the recorder script, and browser contracts (Phase 6)

- Status: Accepted (owner, 2026-10-08: "proceed to phase 6"; choices: a recorder-only script, a new contract project)
- Date: 2026-10-08
- Phase: 6 (Selectors & Recorder)
- Amends: [ADR-0017](0017-browser-automation-provider.md) (no JavaScript in pages: an exception for recording sessions
  only), [ADR-0003](0003-project-structure-and-dependency-direction.md) (a new contract project),
  [ADR-0015](0015-plugin-isolation-and-trust.md) / [ADR-0016](0016-plugin-loading-and-manifest.md) (plugins may implement
  interfaces of a shared contract assembly)

## Context
PRD Phase 6: a user records a simple website interaction (click, type, navigate, select, upload, download) and inserts
the generated `Browser.*` activities into the Studio, with semantic selectors (ADR-0038), not coordinates.

Three facts shape the design:
1. A person must use a real, visible browser while recording. Workflow runs stay headless by default.
2. Seeing which element was clicked or typed into, and generating a selector for it, needs code in the page. ADR-0017
   forbids JavaScript evaluation in pages (no `EvaluateAsync`), for workflow runs.
3. The server (which hosts the Studio) must use the recorder, but Playwright lives only in its plugin; the server does
   not reference the SDK, and a plugin may register only its own types (`PluginRegistrar.RequireOwned`).

## Decision

### 1. Recording sessions
- A recording session is a **separate thing from a workflow run**: it is started by the Studio through the server
  (local mode, the same machine), not by an activity, and never executes a workflow.
- It launches Playwright's Chromium **headed** (a visible window) at an http/https start URL (the same URL rules as
  `Browser.Open`). Closing the window or pressing Stop ends it. One recording session at a time per server.
- The recorded steps are suggestions shown to the user, who edits them and decides what to insert. Nothing recorded is
  ever executed by the recorder.

### 2. The recorder script (the exception to ADR-0017)
- Only recording sessions add a script to their pages: one fixed file, `recorder.js`, embedded in the browser plugin
  (`BrowserContext.AddInitScriptAsync`). It listens to user events (click, input/change, select, file choice, submit),
  computes selector candidates (ADR-0038 §3) and reports them through one exposed binding
  (`BrowserContext.ExposeBindingAsync`) with a per-session random name.
- Nothing else changes: workflow runs (`Browser.*` activities) still never evaluate or inject JavaScript; the
  architecture test that bans `EvaluateAsync` stays, and a new one allows `AddInitScriptAsync`/`ExposeBindingAsync`
  only in the recorder class.
- Threat model: the recorded site's own scripts can call the binding and send fake steps. They can only add suggested
  steps to the list the user reviews; the host validates every field (kinds, lengths, selector syntax by the parser)
  and drops malformed messages. The host re-checks each selector's uniqueness with the browser (Playwright locators,
  not page script).
- **Secrets:** text typed into password fields is never sent to the host: the step records only that a password was
  typed, and the Studio inserts it as a `Browser.TypeText` whose `text` is an In argument (`password`), added to the
  workflow as an argument without a default.
- Uploads record file names only (a browser does not expose paths); the user maps them to files under the plugin's
  `fileRoot`. Downloads record the click that started the download and the suggested file name.

### 3. Browser contracts project
- New `src/MyRPA.Browser.Contracts` (BCL only, plain `net10.0`, no references): the recorder interface
  (`IBrowserRecorder`: start, stop, observe), its options, and the recorded-step model (kind, selector, alternatives,
  value, URL, file names). Referenced by `MyRPA.Server` and by the browser plugin (host-provided, `Private="false"`).
- The plugin loader shares it with plugins like the SDK (`PluginLoadContext.SharedAssemblies`) and lets a plugin
  register a service whose type comes from it (an **extension contract**: a list the host reads, never a replacement of
  a host service). `RequireOwned` stays for every other type.
- Dependency direction: Contracts-like (no references) ← `MyRPA.Browser.Contracts` ← Server, browser plugin.
  `ArchitectureRules` and the overview's project table gain the entry.

### 4. Server and Studio
- Server: `POST /api/recordings` (start URL) → recording id; recorded steps arrive on the tab's existing event stream
  (`recording.step`, `recording.ended`); `DELETE /api/recordings/{id}` stops. Session, Origin and anti-forgery checks as
  for runs. A server without the browser plugin answers 503 with the reason.
- Studio: Record (start URL) → a Recording panel lists the steps live (selector with its alternatives, value, delete);
  Stop; **Insert** places `Browser.Open` (the start URL), the steps and `Browser.Close` at the selection as one undo
  step (and the `password` argument when needed).

## Consequences
- The recorder backend is testable headless: the same session with a test-only headless flag, driven by Playwright as a
  simulated user against local pages.
- A Studio user needs the full Chromium (not only the headless shell) for the visible window; `playwright.ps1 install
  chromium` installs both.
- Phase 6 adds no selector healing, no element picker on existing activities, and no Windows selectors (Phase 7).
