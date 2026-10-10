# MyRPA.Server (local mode)

Status: Web Studio W2; `--web` added in W3 ([ADR-0028](../adr/0028-web-studio-first-slice.md)); `--open`, the bundled
Studio and `move` added in W6 ([ADR-0031](../adr/0031-web-studio-project-and-file-management.md)). Decisions:
- [ADR-0022](../adr/0022-server-control-plane-and-project-structure.md): control plane;
- [ADR-0023](../adr/0023-first-class-execution-events.md): execution events;
- [ADR-0024](../adr/0024-execution-event-streaming-sse.md): streaming;
- [ADR-0025](../adr/0025-local-mode-security.md): local-mode security;
- [ADR-0026](../adr/0026-missing-property-diagnostic-location.md): diagnostic location;
- [ADR-0027](../adr/0027-invoke-workflow-confinement-in-projects.md): InvokeWorkflow confinement.

The server is the control plane the Web Studio ([web-studio.md](web-studio.md)) talks to. It composes the same engine,
activities, storage and plugin host as the CLI, plus `MyRPA.Execution.Hosting`, and runs workflows in-process. With
`--web` it also serves the built Web Studio from its own origin.

## Start

```bash
# once: build the Studio, then the server (the server's build copies web/studio/dist next to it as wwwroot)
(cd web/studio && npm ci && npm run build) && dotnet build MyRPA.sln -c Release
# then one command:
dotnet run --project src/MyRPA.Server -c Release --no-build -- --project samples          # Studio included
dotnet run --project src/MyRPA.Server -c Release --no-build -- --open samples/hello-world.json   # project = its folder
```

Command line: `MyRPA.Server [--projects-root <dir>] [--project <dir>]... [--open <workflow.json>] [--plugin <dir>]... [--plugin-config <file>] [--web <dir>] [--port <n>]`.

- **Projects folder** (`--projects-root`, [ADR-0046](../adr/0046-projects-folder-create-and-delete.md)): every folder in it is a project, after the `--project` folders. The Studio creates projects there and deletes them to its `.trash`. With no project named at all, it is `Documents/Laconi Projects`.
- **Port:** the default is 5310; `0` picks a free port.
- **Web Studio:** the server serves the Studio its build bundled (`wwwroot` next to it, copied from `web/studio/dist`
  when that was built first). `--web <dir>` serves another build instead (must contain `index.html`). Without either,
  `GET /` returns a short text, no UI is served, and the server says so at startup. The .NET build never runs npm.
- **Open a workflow** (`--open <file>`, the WPF Studio's file argument): the file must be inside a `--project` folder;
  given alone, its folder becomes the project. `/api/info` names it and the Studio opens it after connecting.
- **Plugins:** loaded exactly as in the CLI (ADR-0014, ADR-0019). A plugin that fails to load stops startup with exit code 5.
- **Start link:** the server prints a one-time link, `http://127.0.0.1:<port>/?token=…`. Opening it creates the browser
  session. The server does not open a browser itself: starting a process is a banned API (ADR-0012).

## Security (ADR-0025)

- **Loopback only.**
- **Host header:** must be `127.0.0.1`, `localhost` or `[::1]` with the server's port. Anything else gets 400 (DNS
  rebinding).
- **Sessions:** `GET /?token=…` exchanges the one-time token (10-minute lifetime) for an `HttpOnly`, `SameSite=Strict`
  cookie `myrpa_session` and redirects to `/`. Every `/api` request without a valid session gets 401.
- **State-changing requests** (POST, PUT, DELETE) need all of the following, otherwise 403 (or 415 for the content type):
  - `Origin` equal to the server's own origin;
  - the header `X-MyRPA-Request: 1`;
  - `application/json` bodies.
- **No CORS headers**, ever.
- **Every response** carries a Content Security Policy (`default-src 'self'`, `frame-ancestors 'none'`), `nosniff`,
  `X-Frame-Options: DENY` and `Referrer-Policy: no-referrer`. API responses are `no-store`.
- **Request bodies:** at most 5 MB.
- **Web Studio assets** (`--web`) pass through the same checks. The CSP needs no exception: the Vite build has no
  inline script or style.
- **Files:** only `.json` files inside registered projects.
  - Paths are relative with `/`.
  - `..`, empty, hidden (`.name`) and absolute segments are rejected.
  - Links (reparse points) are not followed.
  - `bin`, `obj`, `node_modules` and hidden folders are not listed.

## API

| Method and path | Purpose |
|---|---|
| `GET /` | With a `token`: the start link (session cookie, redirect to `/`). Otherwise the Web Studio's `index.html` (`--web`), or a short text |
| `GET /<file>` | With `--web`: the Studio's static assets. No session needed; hidden and unknown file types are not served |
| `GET /api/info` | Server name, version, `mode: "local"`, supported workflow schema versions (`["1.0", "1.1"]`), project names, and `open` (`{ project, path }` from `--open`, else null) |
| `GET /api/activities` | The activity catalog snapshot (ADR-0020 format, catalog version 1.2: `childLayout`, and per ADR-0042 `valueType`, `default`, `secret` and `sideEffects`), built-in and plugin activities |
| `GET /api/plugins` | Loaded plugins (id, name, version, SHA-256, activities) and their load diagnostics |
| `GET /api/projects` | `{ projects: [{ name, removable }], projectsRoot }`: the `--project` folders, then the projects folder's (`removable`); `projectsRoot` null when there is none (ADR-0046) |
| `POST /api/projects` | `{ name }`: creates an empty project folder in the projects folder → 201 `{ name }`; 400 for a refused name; 409 when taken or without a projects folder |
| `DELETE /api/projects/{project}` | Moves a project of the projects folder to `.trash/<name>-<yyyyMMdd-HHmmss>` → 200 `{ name, trash }`; 404 unknown; 409 for a `--project` or a folder in use |
| `GET /api/projects/{project}/workflows` | `{ workflows, folders }`: workflow files (path, size, modified) and every folder, empty ones too |
| `POST /api/projects/{project}/folders` | `{ path }`: creates a folder (and its parents) inside the project with the file path rules (no `..`, hidden names, backslashes or links) → 201 `{ path }`; 409 when a file or folder of that name exists, 400 for a refused path |
| `GET /api/projects/{project}/workflows/{path}` | The file's JSON; `ETag` header |
| `PUT /api/projects/{project}/workflows/{path}` | Create (`If-None-Match: *`) or update (`If-Match: <etag>`). The body must be a JSON object. Returns 201 or 204 with the new `ETag`; 412 on a conflict; 428 without a precondition. Invalid workflows can be saved; validation is separate. |
| `DELETE /api/projects/{project}/workflows/{path}` | Delete; `If-Match` required |
| `POST /api/projects/{project}/move` | `{ from, to }` with `If-Match` (the source's ETag): renames or moves a file within the project, atomically, content unchanged → 200 `{ path }` and the `ETag`. 409 if `to` exists (never overwrites), 412 on a stale ETag, 428 without `If-Match`, 404 if `from` is gone, 400 for paths the store refuses or `from` = `to` (W6) |
| `POST /api/validate` | `{ document }` → `{ valid, diagnostics: [{ code, severity, message, path, nodeId }] }` from the engine's `WorkflowLoader`. `…properties.<name>` in a path names the property (ADR-0026). |
| `GET /api/expressions/functions` | The expression functions (ADR-0041): `[{ name, minArguments, maxArguments, signature, description }]` |
| `POST /api/expressions/scope` | `{ document, path }` → `{ names: [{ name, kind, type, direction, path }] }`: the arguments, variables and locals visible at `path` (a node or anything inside it), as the loader's validation sees them. Works on documents with errors as far as they can be read |
| `POST /api/expressions/references` | `{ document, path, name }` → `{ declaration, references: [{ path, start, length, declaration }] }`: the declaration `name` resolves to at `path` and every place it is written, with the name's exact characters in each string. Scope-aware: a local of another loop is another declaration; member names and text in strings never match. 400 for a bad document, path or name |
| `POST /api/runs` | `{ project, path, document?, arguments?, argumentText?, timeoutMs?, debug? }` → 202 `{ runId }`. See below. |
| `GET /api/runs/{runId}` | `{ runId, state, lastSequence, result? }`. The result has status, ids, duration, outputs and error. |
| `POST /api/runs/{runId}/cancel` | 202, 404 if the run is unknown, 409 if it already finished |
| `GET /api/runs/{runId}/debug` | A debug run's state (ADR-0040): `{ paused: null or { executionId, parentExecutionId, workflowId, nodeId, activityType, reason, values: [{ name, kind, type, value }] }, breakpoints: [nodeId] }`. The only place paused values appear. 404 for a run that is not a debug run of this session |
| `POST /api/runs/{runId}/debug` | `{ command }`: `continue`, `stepInto`, `stepOver`, `stepOut` (202, 409 if not paused) or `pause` (202, 409 if already paused or finished). 400 for another command, 404 as above |
| `PUT /api/runs/{runId}/breakpoints` | `{ breakpoints: [nodeId] }` replaces a debug run's breakpoints, from its next node on → 204 |
| `POST /api/streams` | 201 `{ streamId }`: one per browser tab |
| `GET /api/streams/{streamId}` | The tab's SSE stream (see below) |
| `POST /api/streams/{streamId}/subscriptions` | `{ runId, afterSequence? }` or `{ recordingId, afterSequence? }`: follow a run or a recording on this stream |
| `DELETE /api/streams/{streamId}/subscriptions/{id}` | Stop following a run or recording |
| `GET /api/recordings` | `{ available, reason }`: whether this server can record (it needs the browser plugin) |
| `POST /api/recordings/generate` | `{ startUrl, steps: [{ kind, selector, element, text, secret, values, url, fileName }] }` → `{ nodes, arguments }`: the browser plugin's activities for the steps the user kept (v1.0 nodes: Open, the steps, Close; a password becomes the In argument `password`). 400 for a step it cannot turn into an activity |
| `POST /api/recordings` | `{ startUrl }` (http/https) → 201 `{ recordingId, startUrl }`: opens a visible recording browser on this machine (ADR-0039). 409 while another recording runs (one at a time), 400 for a bad URL, 503 without the browser plugin |
| `GET /api/recordings/{id}` | `{ recordingId, startUrl, active, endReason?, steps }` (the current steps) |
| `DELETE /api/recordings/{id}` | Stop recording and close its browser → 204 |
| `DELETE /api/streams/{streamId}` | Close the stream |

**Starting runs (`POST /api/runs`):**
- Without `document`, the saved file runs.
- With `document`, an unsaved buffer runs as if it were saved at `path`, so `Core.InvokeWorkflow` resolves from there.
  Confinement is unchanged: the entry workflow's directory (ADR-0012, ADR-0027).
- `arguments` are JSON values, converted by the engine's `WorkflowValues.TryFromJson` to the declared types.
- `argumentText` (W5, ADR-0030) maps input argument names to text as a person typed it. The text is parsed by
  `WorkflowValues.TryParseText`, the rule of the CLI's `--arg` and the WPF run prompt. String is taken verbatim;
  Int, Decimal, Boolean and DateTime use invariant formats; List, Dictionary and Object are parsed as JSON. The Web
  Studio's run dialog uses it, so the browser never converts values. A name may appear in `arguments` or in
  `argumentText`, not both.
- Arguments left out get their declared default. A missing required argument is reported by the engine as a failed
  run with `MYRPA2004` before any node runs.
- An invalid workflow gets 422 with the diagnostics. Unknown or Out arguments, bad values or text, and a name given
  twice get 400.
- `debug: { breakpoints?: [nodeId], pauseAtStart? }` starts a debug run (ADR-0040). Breakpoints are node ids of the run's
  workflow (at most 10,000; an empty id is a 400). The run belongs to the session that started it: only that session can
  read its paused values and send commands; anyone else gets 404. Stop is the usual cancel, which also ends a pause.

## Event stream (ADR-0024)

A stream follows runs and, since Phase 6, recordings (ADR-0039): `recording.step` (`{ sequence, kind, recordingId,
time, step: { sequence, kind, selector, alternatives, element, text?, secret, values, url?, fileName?, replaces? } }`;
a step with `replaces` takes the place of that earlier step) and `recording.ended` (`endReason`: `Stopped`,
`BrowserClosed` or `Failed`). Recordings resume by the same position vector as runs. A recording belongs to the session
that started it. `--recorder-headless` opens recording browsers without a window, and `--recorder-debugging-port <n>` gives them a
local DevTools port so an end-to-end test can act as the user; both are for automated tests only.


One `EventSource` per tab, for any number of runs:

```text
retry: 2000
event: stream.opened
data: {"streamId":"…"}

id: 0=3,1=0
event: node.started
data: {"sequence":3,"kind":"node.started","runId":"…","time":"…","executionId":"…","nodeId":"main","activityType":"Core.Sequence"}
```

- **Data:** each event's `data` is an `ExecutionEventMessage` (`MyRPA.Contracts`). The kinds are `execution.started`,
  `node.started`, `node.completed`, `execution.completed`, `log` and `stream.gap`; debug runs add `debug.paused`
  (`nodeId`, `reason`: `breakpoint`, `step` or `pause`) and `debug.resumed` (`reason`: the command). These two carry ids
  only; the values come from `GET /api/runs/{runId}/debug`.
- **Order:** within a run, sequences are 1, 2, 3… in execution order. Runs are interleaved.
- **Run end:** a run ends with the `execution.completed` that has no `parentExecutionId`.
- **Resuming:** the `id` is the stream's position vector: subscription index = last sequence delivered. After a
  disconnect the browser sends it back as `Last-Event-ID`, and every run resumes exactly there.
- **Missing events:** if the run's replay buffer has already dropped events, a `stream.gap` event says which.
- **Limits:**
  - keep-alive comments every 15 s;
  - at most 32 streams and 64 runs per stream;
  - a disconnected stream is kept for 2 minutes;
  - each connection has a 4,096-event queue; when it is full, the run's events wait for the client. A client that
    takes no event for `SlowClientTimeout` (10 s) is disconnected and resumes by cursor (ADR-0024, amended).
- **Ownership:** streams belong to the session that created them.
- **Logs:** workflow log messages reach runs through the engine's logging scope. The server's logging filters let
  `MyRPA.Workflow.Log` through at Information for that purpose, and keep it off the console.
