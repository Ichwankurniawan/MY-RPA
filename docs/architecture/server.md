# MyRPA.Server (local mode)

Status: Web Studio W2; `--web` added in W3 ([ADR-0028](../adr/0028-web-studio-first-slice.md)). Decisions:
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
dotnet run --project src/MyRPA.Server -- --project samples --port 0
dotnet run --project src/MyRPA.Server -- --project samples --web web/studio/dist --port 0   # with the Web Studio
```

Command line: `MyRPA.Server --project <dir> [--project <dir>]... [--plugin <dir>]... [--plugin-config <file>] [--web <dir>] [--port <n>]`.
- **Port:** the default is 5310; `0` picks a free port.
- **Web Studio:** `--web <dir>` serves the built Studio (`web/studio/dist`, must contain `index.html`). Without it,
  `GET /` returns a short text and no UI is served.
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
| `GET /api/info` | Server name, version, `mode: "local"`, supported workflow schema versions, project names |
| `GET /api/activities` | The activity catalog snapshot (ADR-0020 format), built-in and plugin activities |
| `GET /api/plugins` | Loaded plugins (id, name, version, SHA-256, activities) and their load diagnostics |
| `GET /api/projects` | Registered projects |
| `GET /api/projects/{project}/workflows` | Workflow files: path, size, modified |
| `GET /api/projects/{project}/workflows/{path}` | The file's JSON; `ETag` header |
| `PUT /api/projects/{project}/workflows/{path}` | Create (`If-None-Match: *`) or update (`If-Match: <etag>`). The body must be a JSON object. Returns 201 or 204 with the new `ETag`; 412 on a conflict; 428 without a precondition. Invalid workflows can be saved; validation is separate. |
| `DELETE /api/projects/{project}/workflows/{path}` | Delete; `If-Match` required |
| `POST /api/validate` | `{ document }` → `{ valid, diagnostics: [{ code, severity, message, path, nodeId }] }` from the engine's `WorkflowLoader`. `…properties.<name>` in a path names the property (ADR-0026). |
| `POST /api/runs` | `{ project, path, document?, arguments?, timeoutMs? }` → 202 `{ runId }`. See below. |
| `GET /api/runs/{runId}` | `{ runId, state, lastSequence, result? }`. The result has status, ids, duration, outputs and error. |
| `POST /api/runs/{runId}/cancel` | 202, 404 if the run is unknown, 409 if it already finished |
| `POST /api/streams` | 201 `{ streamId }`: one per browser tab |
| `GET /api/streams/{streamId}` | The tab's SSE stream (see below) |
| `POST /api/streams/{streamId}/subscriptions` | `{ runId, afterSequence? }`: follow a run on this stream |
| `DELETE /api/streams/{streamId}/subscriptions/{runId}` | Stop following a run |
| `DELETE /api/streams/{streamId}` | Close the stream |

**Starting runs (`POST /api/runs`):**
- Without `document`, the saved file runs.
- With `document`, an unsaved buffer runs as if it were saved at `path`, so `Core.InvokeWorkflow` resolves from there.
  Confinement is unchanged: the entry workflow's directory (ADR-0012, ADR-0027).
- `arguments` are JSON values, converted by the engine's `WorkflowValues.TryFromJson` to the declared types.
- An invalid workflow gets 422 with the diagnostics. Unknown arguments or bad values get 400.

## Event stream (ADR-0024)

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
  `node.started`, `node.completed`, `execution.completed`, `log` and `stream.gap`.
- **Order:** within a run, sequences are 1, 2, 3… in execution order. Runs are interleaved.
- **Run end:** a run ends with the `execution.completed` that has no `parentExecutionId`.
- **Resuming:** the `id` is the stream's position vector: subscription index = last sequence delivered. After a
  disconnect the browser sends it back as `Last-Event-ID`, and every run resumes exactly there.
- **Missing events:** if the run's replay buffer has already dropped events, a `stream.gap` event says which.
- **Limits:**
  - keep-alive comments every 15 s;
  - at most 32 streams and 64 runs per stream;
  - a disconnected stream is kept for 2 minutes;
  - a client too slow for the 4,096-event queue is disconnected and resumes by cursor.
- **Ownership:** streams belong to the session that created them.
- **Logs:** workflow log messages reach runs through the engine's logging scope. The server's logging filters let
  `MyRPA.Workflow.Log` through at Information for that purpose, and keep it off the console.
