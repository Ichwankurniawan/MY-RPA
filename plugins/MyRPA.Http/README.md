# MyRPA.Http

The `Http.Request` activity (Phase 7, [ADR-0042](../../docs/adr/0042-enterprise-automation-activities.md)). A product
plugin: it references only the Automation SDK and no packages; HTTP comes from the .NET base library. `HttpClient` is
banned in `src` (ADR-0008), so network access lives in this plugin, which the operator chooses to load. It runs
in-process and is fully trusted (ADR-0015); its load context is not a sandbox.

## Settings (plugin configuration, ADR-0019)

| Setting | Default | Meaning |
|---|---|---|
| `allowedHosts` | empty (any host) | Host names requests and every redirect may reach, comma-separated; `*.example.com` matches sub-domains |
| `maxResponseBytes` | 10485760 (10 MB) | The largest response body read (after decompression) |
| `maxRedirects` | 5 | The most redirects followed (0 follows none) |
| `defaultTimeoutMs` | 30000 | The timeout when a node gives no `timeoutMs` |

**Server-side request forgery:** with `allowedHosts` empty, a workflow can reach any host the robot's machine can,
internal addresses included. Set `allowedHosts` wherever workflows or their inputs are not fully trusted.

## `Http.Request`

| Property | Kind | Meaning |
|---|---|---|
| `method` | choice | GET (default), POST, PUT, PATCH, DELETE, HEAD |
| `url` | expression, String, required | Absolute http/https URL; a URL with a user name or password is refused |
| `headers` | expression, Dictionary | Header name → value, for example a variable with a default; `Authorization`, `Host`, `Content-Length`, `Transfer-Encoding` and `Connection` are refused |
| `body` | expression, Any | A String is sent as `text/plain`, any other value as JSON |
| `auth` | choice | None (default), Bearer, Basic, ApiKey |
| `token` | expression, String, **secret** | Bearer token or API key |
| `username` / `password` (**secret**) | expression, String | Basic credentials |
| `apiKeyHeader` | text | The API key's header (default `X-Api-Key`) |
| `timeoutMs` | expression, Int | Default 30000; also capped by the run's deadline |
| `failOnErrorStatus` | expression, Boolean | Default true: 400 or above fails with `HttpStatus` and sets no output; false keeps the status and body for the workflow to inspect |
| `parseJson` | expression, Boolean | Default true: an `application/json` or `+json` response becomes a workflow value |
| `status`, `responseHeaders`, `responseBody` | assignment targets | Int; Dictionary with lower-case names; value, text or null |

Secret properties must name an argument or variable (validation `MYRPA1066`); a token is never written in the
workflow file.

## Security and reliability

- TLS certificate validation is the platform's and is never turned off.
- Redirects are followed by the activity, not the client: every hop is checked against `allowedHosts`, a redirect to
  another scheme or from https to http is refused (`RedirectNotAllowed`), and credentials are sent only to the
  original origin (scheme, host, port), never after a redirect to another one.
- Cookies are off, so concurrent runs never share them; the one `HttpClient` lives as long as the plugin and is
  disposed by the host.
- The response body is read in chunks and fails with `ResponseTooLarge` past `maxResponseBytes`; JSON deeper than 64
  levels fails with `InvalidJson`.
- Messages name the method and the URL without its query string; they never contain header values, tokens or
  passwords. The plugin writes no logs.
- Cancelling the run cancels the request; a timeout fails with `Timeout`.

| errorType | When |
|---|---|
| `InvalidUrl` | Not an absolute http/https URL, or one with credentials in it |
| `HostNotAllowed` | The host or a redirect's host is not in `allowedHosts` (nothing is sent) |
| `HttpStatus` | Status 400 or above with `failOnErrorStatus` |
| `HttpConnection` | DNS, refused connection, TLS failure, connection reset |
| `Timeout` | No complete response within the timeout |
| `ResponseTooLarge` | The body is larger than `maxResponseBytes` |
| `TooManyRedirects` / `RedirectNotAllowed` | Redirect limits |
| `InvalidJson` | A JSON response that is not valid JSON |
| `InvalidInput` | A property of the wrong kind, a missing credential, a refused header |
