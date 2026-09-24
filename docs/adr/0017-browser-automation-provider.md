# ADR-0017: Browser automation provider (Playwright plugin)

- Status: Accepted
- Date: 2026-09-24
- Phase: 4
- Builds on: ADR-0013 (SDK), ADR-0014/0016 (plugin loading), ADR-0015 (trust)

## Context
PRD Phase 4 asks for the first real automation provider: browser automation with Playwright, following PRD 7.2
(`Workflow Activity → IBrowserProvider → PlaywrightProvider → Browser → BrowserContext → Page`). The Phase 3 SDK has to
support it without redesign, and Playwright must stay out of Core, Workflow, Runtime and the CLI.

## Decision

### Packaging
- `plugins/MyRPA.Browser.Playwright` is an ordinary plugin (id `MyRPA.Browser.Playwright`, version 0.1.0, SDK 1.0).
  - It references `MyRPA.Sdk` (host-provided) and **Microsoft.Playwright 1.63.0**, which is plugin-private.
  - It declares the capabilities `Process` (the Node.js driver and the browsers), `Network` and `FileSystem`.
- The architecture tests forbid `Microsoft.Playwright` in every project except this plugin.
- A build produces a complete plugin directory **for the build machine's platform**: 124 files and 104 MB on Windows
  x64, most of it the Node.js driver.
- Hosts must keep reflection-based `System.Text.Json` enabled, so no trimmed or AOT hosts (ADR-0016).

### Contract, defined inside the plugin
- `IBrowserProvider : IAutomationProvider` has a single operation, `LaunchAsync(sessionId, options, limits)`.
  - Plugin lifetime.
  - It owns one Playwright driver, a Node.js process started on first use and stopped at host shutdown.
- `IBrowserSession` represents one browser, context and page:
  - `NavigateAsync`, `LocateAsync`, `WaitForAsync`, `DownloadAsync`, `CloseAsync`;
  - metadata: id, browser name and version, headless, opened-at.
- `IBrowserElement : IAutomationElement` adds `AppendTextAsync`, `SelectOptionsAsync` and `SetInputFilesAsync`.
- `OperationLimits(Timeout, CancellationToken)` accompanies every operation.
- No SDK type changed. Browser-specific types stay in the plugin until a second consumer needs them; a shared
  "browser contract" assembly is a Phase 6 question.
- The session does not implement the SDK's immediate `ISelectorResolver` yet, because no activity needs "what matches
  now" and waiting (Playwright auto-waiting) is the right default.

### Sessions and lifetime
- **One browser process per session.** Sessions never share cookies, storage or pages; concurrent runs are isolated.
- `BrowserSessions` is a **run-lifetime** service:
  - `Browser.Open` registers a session with id `browser-<n>`, unique within the run;
  - `session` properties select one, and without `session` the only open session is used;
  - `Browser.Close` closes one.
- **When the run ends**, in any status (succeeded, failed, cancelled or timed out), every session still open is closed:
  context first, then browser, bounded to 10 s per session. **No browser outlives the run that opened it.**
- A launch interrupted by cancellation closes its browser as soon as it exists, so no process is orphaned.
- Sessions are shared with workflows invoked through `Core.InvokeWorkflow`, because they share the run's services.

### Activities
- The 11 activities are: `Browser.Open`, `Browser.Navigate`, `Browser.Click`, `Browser.TypeText`,
  `Browser.GetText`, `Browser.GetAttribute`, `Browser.WaitForElement`, `Browser.SelectOption`, `Browser.UploadFile`,
  `Browser.DownloadFile` and `Browser.Close`.
- They are deterministic, with no AI and no retries beyond Playwright's actionability waiting.
- **Element operations are strict:** exactly one element must match.
- **Timeouts:** every activity accepts `timeoutMilliseconds`. The default is the plugin setting
  `defaultTimeoutMilliseconds` (30 000).
  - The effective timeout is also capped at the time left before the run's deadline, plus 250 ms. When the run's
    timeout is the limiting one, the run ends `TimedOut` rather than failing the node.
- **Cancellation:** every Playwright call is awaited with the run's token. When it fires, the activity stops waiting
  and the run's clean-up closes the browser; Playwright calls themselves cannot be cancelled.

### Selectors (provisional; the final format is Phase 6)
- The `selector` property is Text using this syntax:
  - `css=…` (the default when there is no prefix);
  - `xpath=…` (the default when the text starts with `/` or `(`);
  - `text=…`;
  - `role=<aria role>` or `role=<role>|<exact accessible name>`;
  - steps joined by ` >> `, each searching within the previous match.
- It is parsed into the SDK `Selector`, with provider `Browser.Playwright` and strategies `Css`, `XPath`, `Text`,
  `Role`.
- There is no selector generation, healing, recording or editor; those are Phase 6.

### Errors (the errorType is the contract; Playwright exceptions are only inner exceptions)

| Situation | errorType |
|---|---|
| No element matches within the timeout | `ElementNotFound` |
| Element matched but never became actionable, visible, hidden or detached in time | `ElementTimeout` |
| More than one element matches | `AmbiguousMatch` |
| Selector syntax invalid (ours or the browser's) | `InvalidSelector` |
| Browser or driver not installed | `ProviderUnavailable` (message includes the install command) |
| Browser failed to launch | `BrowserLaunchFailed` |
| Network error, HTTP status 400 or above, aborted navigation | `NavigationFailed` |
| URL not absolute http, https or about:blank | `InvalidUrl` |
| Session closed or browser disconnected | `SessionClosed` |
| Unknown session id, or none (or several) open without `session` | `SessionNotFound` |
| Navigation or other non-element operation exceeded its timeout | `OperationTimeout` |
| Click did not start a download, or the download failed | `DownloadFailed` |
| File outside the file root, or a link | `FileAccessDenied` |
| Upload file missing / download target exists without `overwrite` | `FileNotFound` / `FileAlreadyExists` |
| Invalid property value (e.g. timeout ≤ 0) | `InvalidArgument` |
| Anything else | `BrowserError` |

Cancellation and the run's timeout are never an errorType: the run ends `Cancelled` or `TimedOut` (ADR-0013).

### Security
- **No JavaScript evaluation.**
  - No activity calls `EvaluateAsync` or exposes page scripting.
  - Pages run their own scripts as in any browser, but workflows cannot inject code.
- **URLs:** only `http`, `https` and `about:blank`. `file:` and other schemes are refused, so a workflow cannot read
  local files through the browser.
- **Files:** uploads and downloads are confined to one root.
  - The root is the plugin setting `fileRoot`, or the host's working directory by default (the CLI cannot yet pass
    plugin settings).
  - Paths are resolved against the root and must stay inside it.
  - Links (symlinks, junctions) anywhere between the root and the file are refused.
  - Downloads never overwrite unless `overwrite` is true, and the target directory must exist.
  - Downloads have **no size limit** (known issue).
- **Isolation:** each session is its own browser process with a fresh context. Nothing is persisted between sessions:
  no user data directory, cookies or cache.
- **Trust:** the plugin is fully trusted code (ADR-0015). It starts processes (Node.js and Chromium) and uses the
  network, as its declared capabilities say.

### Browser installation
- Playwright 1.63.0 needs **Chromium 153.0.8010.12 and Chrome Headless Shell, revision 1243**.
- They are installed per user into the Playwright cache (`%LOCALAPPDATA%\ms-playwright`, `~/.cache/ms-playwright`)
  with the script the build puts in the plugin directory:
  `pwsh plugins/MyRPA.Browser.Playwright/bin/<Configuration>/net10.0/playwright.ps1 install chromium`.
  Linux CI adds `--with-deps` for system libraries.
- Tests and hosts do **not** download browsers implicitly. A missing browser fails with `ProviderUnavailable`, and the
  message names the command.

## Alternatives
- **One shared browser with a context per session.** Cheaper, but a browser crash or leak would affect all runs.
  Rejected for Phase 4; it can come later as a provider option.
- **Implicit sessions only** (one browser per run). Too restrictive for multi-window scenarios. Explicit ids with an
  implicit default cover both.
- **Selector as an expression.** Would allow dynamic selectors, but hurts readability and the future Phase 6 format.
  Selectors are Text for now.
- **Firefox and WebKit.** Deferred: not installed or tested, so not claimed.
- **Evaluate JavaScript activity.** Rejected: arbitrary code execution in a page is out of Phase 4 scope and needs its
  own security review.

## Consequences
- Browser automation is added without touching Core, Workflow, Runtime or the SDK. The loader changed only as ADR-0016
  describes.
- CI must install Chromium before running the browser tests. Only Windows x64 is tested; Linux and macOS are
  unverified.
- Before Phase 6, the recorder and Studio will need `IBrowserProvider` visible outside the plugin: the shared
  technology-contract question from the Phase 3 review (I2).
