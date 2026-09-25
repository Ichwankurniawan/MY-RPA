# ADR-0025: Local-mode security for MyRPA.Server

- Status: Accepted, implemented and tested in W2 (`MyRPA.Server`, `SecurityTests`)
- Date: 2026-09-25
- Phase: Web Studio W0
- Builds on: ADR-0008 (secure-by-default), ADR-0015 (plugin trust)
- Related: ADR-0022, ADR-0024

## Context
In local mode, `MyRPA.Server` runs on the user's machine and executes workflows in-process, including browser automation. Any website the user visits can make the browser send requests to `127.0.0.1`. DNS-rebinding attacks can also make a hostile domain resolve to loopback. A local server that runs automation is therefore a high-value target, and it must be protected from the start.

## Decision
- **Loopback only:**
  - bind to `127.0.0.1`, or `[::1]`;
  - a random or configured port;
  - never `0.0.0.0` in local mode.
- **Session bootstrap:**
  - Startup prints and opens `http://127.0.0.1:<port>/?token=<one-time random token>`.
  - The server exchanges the token once for an `HttpOnly`, `SameSite=Strict` session cookie, then redirects to remove the token from the URL.
  - The token expires after first use or a short time-to-live.
  - A missing or invalid session gets `401`, with no information in the response.
- **Host header allow-list:** `127.0.0.1:<port>`, `localhost:<port>`, `[::1]:<port>`. Anything else is rejected, which defeats DNS rebinding.
- **Origin check:**
  - Every state-changing request must have an `Origin` equal to the server's own origin.
  - The event stream (ADR-0024) and all GET endpoints are side-effect free, protected by the `SameSite=Strict` cookie and the Host check.
- **Anti-forgery:** state-changing requests require `Content-Type: application/json` plus an anti-forgery header, which a cross-site form cannot send.
- **No CORS.** The Studio is same-origin; the server sends no CORS headers.
- **Content Security Policy:** `default-src 'self'`; no inline script; no `eval`; `frame-ancestors 'none'`. React renders workflow and plugin text only as text.
- **Files:**
  - The API exposes only files inside registered project roots.
  - Paths are normalized and confined, the same way as ADR-0012.
  - There are no endpoints that take arbitrary absolute paths.
- **Plugins:**
  - Loaded only from the operator's command line or plugin configuration file (ADR-0019).
  - No upload, install or enable endpoints.
  - Plugins stay fully trusted (ADR-0015).
- **Execution:**
  - The same limits as the CLI: timeouts, invocation depth, file policies.
  - A configurable limit on concurrent runs (browser sessions are processes).
- **Hosted mode (later):** OIDC, per-project roles with a separate *Run* permission, and TLS at a reverse proxy. It is designed separately; nothing in local mode may assume "localhost means trusted".

## Implementation notes (W2)
- **Anti-forgery:** the anti-forgery header is `X-MyRPA-Request: 1`.
- **Cookie:** the session cookie is `myrpa_session`.
- **Start link:** the server prints the start link but does not open a browser. Launching one would need `Process.Start`,
  which is banned (ADR-0012). The user opens the link.
- **Host check:** compares against the port the connection arrived on, so it also works with `--port 0`.

## Consequences
- W2 must include security tests for:
  - a wrong Host header;
  - a cross-origin POST;
  - a missing anti-forgery header;
  - token reuse;
  - path traversal;
  - the absence of CORS headers.
- A browser bookmark without a session needs a new start URL (or a token printed again). This inconvenience is accepted.
