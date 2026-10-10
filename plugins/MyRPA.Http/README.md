# MyRPA.Http

`Http.Request` (Phase 7, [ADR-0042](../../docs/adr/0042-enterprise-automation-activities.md)), with retries, and
`Http.Download`, `Http.Upload` and `Chat.Post` (Phase 7.1, [ADR-0043](../../docs/adr/0043-more-enterprise-integrations.md)). A product
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
| `fileRoot` | not set | The only folder tree `Http.Download` writes to and `Http.Upload` reads from; not set, they refuse every path (`FileAccessDenied`) |
| `maxDownloadBytes` | 104857600 (100 MB) | The largest file a download writes |
| `maxUploadBytes` | 104857600 (100 MB) | The most file bytes one upload sends |

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
| `timeoutMs` | expression, Int | Default 30000, per attempt; also capped by the run's deadline |
| `retries`, `retryDelayMs` | expression, Int | Default 0 and 1000: repeat after a connection error, 429 or 500/502/503/504; the wait doubles (at most 60 s); `Retry-After` is honoured, and one longer than 60 s ends the retries |
| `retryUnsafe` | expression, Boolean | Default false: only GET, HEAD, PUT and DELETE are repeated; true repeats POST and PATCH too |
| `failOnErrorStatus` | expression, Boolean | Default true: 400 or above fails with `HttpStatus` and sets no output; false keeps the status and body for the workflow to inspect |
| `parseJson` | expression, Boolean | Default true: an `application/json` or `+json` response becomes a workflow value |
| `status`, `responseHeaders`, `responseBody` | assignment targets | Int; Dictionary with lower-case names; value, text or null |

Secret properties must name an argument or variable (validation `MYRPA1066`); a token is never written in the
workflow file.

## `Http.Download`

GET `url` to the file `path` (relative to `fileRoot`). The body is streamed to a hidden `.download` file beside the
target and moved into place only when complete, so a failed or too large download leaves no file. Properties: `url`,
`path`, `overwrite` (default false: an existing file fails with `FileAlreadyExists`), the authentication and retry
properties above, and the outputs `status`, `bytes` and `file` (the path relative to `fileRoot`). A status of 400 or
above fails with `HttpStatus`; more than `maxDownloadBytes` fails with `ResponseTooLarge`.

## `Http.Upload`

A `multipart/form-data` request (`method` POST or PUT). `files` is a Dictionary of form field → path relative to
`fileRoot`; `fields` a Dictionary of text fields. Every file is resolved and the total checked against
`maxUploadBytes` (`FileTooLarge`) before anything is sent; the files are opened again for each attempt and for a
307/308 redirect. The response properties are those of `Http.Request`. Retries repeat PUT, and POST only with
`retryUnsafe`.

## `Chat.Post`

Posts `message` (with an optional `title` and `color` as `#RRGGBB`) to an incoming webhook: `platform` Teams (an
Adaptive Card), Slack (`text`, or an attachment with a colour), Discord (`content`, or an embed) or Generic
(`{ "title", "message", "color" }`). `webhookUrl` is a **secret** property, since its path is the credential:
messages name only the webhook's host, redirects are not followed (`RedirectNotAllowed`), and `retries` repeats only
a 429 (honouring `Retry-After`), so a message is never posted twice. Output: `status`.

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
