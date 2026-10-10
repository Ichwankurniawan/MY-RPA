# Enterprise automation activities (Phase 7)

The activity catalog added in Phase 7 ([ADR-0042](../adr/0042-enterprise-automation-activities.md), plan:
[enterprise-activities-plan.md](enterprise-activities-plan.md)): data built-ins in the engine, and three product
plugins for work with side effects. Phase 7.1 ([ADR-0043](../adr/0043-more-enterprise-integrations.md), plan:
[enterprise-activities-7-1-plan.md](enterprise-activities-7-1-plan.md)) extends them and adds the documents, email,
database and SFTP plugins. Every activity is described by catalog 1.2 metadata (value types, defaults,
secret properties, side effects), so the Studio builds its editors from the catalog with no per-activity code.

| Where | Activities | Side effect | Package |
|---|---|---|---|
| Built-ins (`MyRPA.Activities`) | `Core.Text.*`, `Core.Json.*`, `Core.Date.*`, `Core.Collection.*` | none | none |
| `plugins/MyRPA.Files` | `File.*` (with `File.Hash`, `File.WaitFor` in 7.1), `Folder.Create`, `Csv.*`, `Json.ReadFile`/`WriteFile`, `Xml.ReadFile`, `Zip.Create`/`Zip.Extract` (7.1) | FileSystem | none (.NET `System.IO.Compression`) |
| `plugins/MyRPA.Http` | `Http.Request`, `Http.Download`, `Http.Upload`, `Chat.Post` (7.1) | Network (Download/Upload also FileSystem) | none (.NET `HttpClient`) |
| `plugins/MyRPA.Spreadsheet` | `Excel.GetSheets`, `Excel.ReadRange`, `Excel.WriteRange`, `Excel.AppendRows`/`Excel.ClearRange` (7.1) | FileSystem | `DocumentFormat.OpenXml` 3.5.1 (this plugin only) |
| `plugins/MyRPA.Documents` (7.1) | `Pdf.ReadText`, `Pdf.GetInfo` | FileSystem | `PdfPig` 0.1.16 (this plugin only) |
| `plugins/MyRPA.Database` (7.1) | `Db.Query`, `Db.Execute`, `Db.Scalar` | Network | `Microsoft.Data.Sqlite` 10.0.12, `Microsoft.Data.SqlClient` 7.1.1, `Npgsql` 10.0.3 (this plugin only) |
| `plugins/MyRPA.Sftp` (7.1) | `Sftp.List`, `Sftp.Download`, `Sftp.Upload`, `Sftp.Delete`, `Sftp.Move`, `Sftp.CreateFolder` | Network (Download/Upload also FileSystem) | `SSH.NET` 2026.0.0 (this plugin only) |
| `plugins/MyRPA.Scripting` (ADR-0045) | `Code.JavaScript` (confined, no files or network), `Code.Python` (opt-in, not sandboxed) | JavaScript: none; Python: FileSystem, Network | `Jint` 4.17.0 (this plugin only) |
| `plugins/MyRPA.Email` (7.1) | `Email.Send`, `Email.Read`, `Email.SaveAttachments`, `Email.MarkRead`, `Email.Move` | Network (Send and SaveAttachments also FileSystem) | `MailKit` 4.18.1 with `MimeKit` (this plugin only) |

The plugins are loaded like any plugin: `--plugin <directory>` (repeatable) or a plugin configuration file with their
settings (ADR-0019). Plugins run in-process with full trust (ADR-0015); a load context is not a sandbox. The limits
below are what the activities enforce themselves.

## 1. Secret properties

A property marked secret in the catalog (`token` and `password` of the HTTP activities, `Chat.Post` `webhookUrl`, the PDF, email, database and SFTP `password`, the SFTP key `passphrase`) must name an argument or variable; a
literal, or an expression without a name, is rejected by validation with `MYRPA1066`. Give the value at run time:
`myrpa run flow.json --arg apiToken=...`, the Studio's run dialog, or an `In` argument of an invoking workflow.

The debugger shows the values of names that feed a secret property as `••••` (ADR-0043). On the command line, pass a
secret with `--arg-env apiToken=MY_TOKEN_VARIABLE` or `--arg-file apiToken=token.txt` rather than `--arg`, whose value
is visible in the process list. Until the credential provider of Phase 11 the value lives in an ordinary variable during
the run. Activities never write a secret to logs, events or error messages.

## 2. Data built-ins (no side effects)

| Type | Inputs | Output `result` | errorType |
|---|---|---|---|
| `Core.Text.Split` | `text`, `separator`, `removeEmpty` (false) | List of String | `InvalidInput` |
| `Core.Text.Join` | `items`, `separator` | String | `InvalidInput` |
| `Core.Text.Match` | `text`, `pattern` (.NET regex), `all` (false), `ignoreCase` (false) | the match (`value`, `index`, `groups`), a List of them, or null | `InvalidPattern`, `Timeout` (1 s) |
| `Core.Text.Replace` | `text`, `pattern`, `replacement` (`$1` for groups), `ignoreCase` | String | `InvalidPattern`, `Timeout` |
| `Core.Json.Parse` / `Core.Json.Serialize` | `text` / `value`, `indented` | value / String | `InvalidJson` (also nesting deeper than 64) |
| `Core.Date.Parse` | `text`, `format` (exact, optional), `timeZone` (IANA, UTC) | DateTime | `InvalidFormat`, `InvalidTimeZone` |
| `Core.Date.Format` | `value`, `format`, `timeZone` | String | `InvalidFormat`, `InvalidTimeZone` |
| `Core.Date.Add` | `value`, `months`, `days`, `hours`, `minutes`, `seconds` | DateTime | `InvalidInput` |
| `Core.Date.Difference` | `from`, `to`, `unit` (Days, Hours, Minutes, Seconds) | Decimal | `InvalidInput` |
| `Core.Collection.Sort` | `items`, `key` (a column), `descending` | List | `InvalidInput` (mixed kinds) |
| `Core.Collection.Filter` / `Core.Collection.Find` | `items`, `key`, `operator` (Equals, NotEquals, Contains, GreaterThan, LessThan, IsNull, IsNotNull), `value` | List / first match or null (+ `index`, -1 when none) | `InvalidInput` |
| `Core.Collection.Merge` | `first`, `second`, `distinct` (false) | List | `InvalidInput` |

Write regular expressions as raw strings, which have no escapes: `r'\d+'` (ADR-0043). In an ordinary string a
backslash is itself escaped, so the same pattern would be `'\\d+'`.

## 3. Files (`MyRPA.Files`)

Settings: `fileRoot`, `maxFileBytes` (50 MB), `maxItems` (10000), `maxRows` (100000), `maxExtractBytes` (500 MB, 7.1). Details:
[plugins/MyRPA.Files/README.md](../../plugins/MyRPA.Files/README.md).

File policy (`MyRPA.Sdk.Files.FileRootPolicy`, shared with the Excel plugin):
- A path is relative to `fileRoot` (or absolute inside it) and is normalized. A path that leaves the root is refused
  (`FileAccessDenied`), and so is one that goes through a symbolic link or junction.
- Nothing is replaced silently: writing to an existing file needs `overwrite` (`FileAlreadyExists` otherwise).
  `File.Delete` deletes one file and never a folder.
- Messages name the path as the workflow wrote it, never the absolute root.
- The check runs when the path is resolved; a link created by another process between the check and the use is not
  detected. Give the robot a root that only its account can write to.

CSV follows RFC 4180. `Csv.Write` makes text starting with `= + - @` inert for spreadsheets (a leading `'`) unless
`protectFormulas` is false. `Xml.ReadFile` refuses DTDs and external entities and never resolves URLs.

ZIP (7.1): `Zip.Extract` checks every entry before writing anything. It refuses entries that would land outside the
destination (zip slip: `..`, absolute paths, drive letters) with `FileAccessDenied`. It also refuses archives with more
than `maxItems` files (`TooManyItems`) or more than `maxExtractBytes` in total (`FileTooLarge`, zip bombs). The bytes
actually written are counted too, since sizes in an archive can lie. `File.WaitFor` polls on the run's clock and never
waits past the run's deadline.

## 4. HTTP (`MyRPA.Http`)

Settings: `allowedHosts`, `maxResponseBytes` (10 MB), `maxRedirects` (5), `defaultTimeoutMs` (30000), and for
`Http.Download`/`Http.Upload` (7.1) `fileRoot` (not set: they refuse every path), `maxDownloadBytes` and
`maxUploadBytes` (100 MB each). Details: [plugins/MyRPA.Http/README.md](../../plugins/MyRPA.Http/README.md).

Phase 7.1 (ADR-0043):
- **Retries:** `retries` (0–10) and `retryDelayMs` repeat an attempt after a connection error, 429 or 500/502/503/504,
  doubling the wait (at most 60 s) and honouring `Retry-After` (a longer one ends the retries). Only GET, HEAD, PUT and
  DELETE are repeated unless `retryUnsafe`; client errors and timeouts never are. `timeoutMs` applies per attempt.
- **Download:** streamed to a hidden file beside the target and moved into place only when complete; never larger
  than `maxDownloadBytes`; never replaces a file without `overwrite`.
- **Upload:** a multipart form of files (from `fileRoot`) and text fields; files are checked against
  `maxUploadBytes` before anything is sent and are read again for each attempt or 307/308 redirect.
- **Chat.Post:** Teams (Adaptive Card), Slack, Discord or a generic JSON webhook. The webhook URL is a secret:
  messages name only its host, redirects are not followed, and only a 429 is repeated.

Security:
- **Hosts:** set `allowedHosts` against server-side request forgery; every redirect hop is checked against it too.
- **TLS:** certificate validation is never turned off.
- **Redirects:** credentials (`auth`) go only to the original origin. Redirects from https to http, or to another
  scheme, are refused.
- **Cookies:** off, so no state is shared between runs.
- **Limits:** the response body is read up to `maxResponseBytes`. A timeout (capped by the run's deadline) and
  cancellation stop the request.
- **Errors:** messages show the URL without its query string; with `failOnErrorStatus`, a status of 400 or above
  fails before anything is assigned.

## 5. Excel (`MyRPA.Spreadsheet`)

Settings: `fileRoot`, `maxFileBytes` (50 MB), `maxRows` (100000), `maxCells` (2000000). Details:
[plugins/MyRPA.Spreadsheet/README.md](../../plugins/MyRPA.Spreadsheet/README.md).

Excel is never started. Reading streams the sheet and gives typed values (dates from number formats, cached formula
results). Writing changes only the written cells and never creates formulas from text. It replaces the file in one
step, so a failure leaves the previous workbook untouched.

## 5a. Documents (`MyRPA.Documents`, 7.1)

Settings: `fileRoot` (the host's working directory by default, like the Files plugin), `maxFileBytes` (50 MB),
`maxPages` (2000), `maxTextChars` (10 million). Details:
[plugins/MyRPA.Documents/README.md](../../plugins/MyRPA.Documents/README.md).

`Pdf.ReadText` returns the text of all pages, or of pages such as `1-3, 5` or `2-`, in reading order. Pages are
separated by a blank line, and `pageTexts` holds each page's text. Pair it with `Core.Text.Match` to pull out an invoice
number or an amount. `Pdf.GetInfo` returns the page count and the document properties.

A PDF is untrusted input:
- it is read only within the file root and up to `maxFileBytes`;
- any parser failure is `InvalidDocument`;
- a PDF that needs a password, or got the wrong one, fails with `EncryptedDocument`;
- the password is a secret property and never appears in messages or logs.

A scanned PDF holds images, not text, and gives empty text: OCR is not part of 7.1.

## 5b. Email (`MyRPA.Email`, 7.1)

Settings: `smtpHost`/`smtpPort`/`smtpSecurity` (StartTls by default), `imapHost`/`imapPort`/`imapSecurity` (SslOnConnect),
`fileRoot`, `defaultFrom`, `maxMessageBytes` (25 MB), `maxMessages` (100), `timeoutMs`. Details:
[plugins/MyRPA.Email/README.md](../../plugins/MyRPA.Email/README.md).

Security:
- **Servers:** they are named only in the plugin configuration; a workflow chooses accounts, not servers.
- **TLS:** it is required by default (a server without STARTTLS is refused) and certificates are always validated.
  `None` is an explicit operator choice for a trusted local relay.
- **Password:** it is a secret property and never appears in messages or logs.
- **Attachments:** they are read from and saved under `fileRoot` only. Their names are cleaned and nothing is
  overwritten unless asked.

Message ids carry the folder's UIDVALIDITY, so an id never silently names another message after the server rebuilds
the folder.

The IMAP tests run against a real server (GreenMail) when `MYRPA_TEST_MAIL` is set, as in the CI `Integration services`
job; otherwise they are reported as skipped.

## 5c. Database (`MyRPA.Database`, 7.1)

Connections are named in the plugin configuration (`connection.NAME.provider`, `.connectionString` without a password,
`.readOnly`), with `commandTimeoutSeconds` (30) and `maxRows` (10000). Details:
[plugins/MyRPA.Database/README.md](../../plugins/MyRPA.Database/README.md).

- **No SQL injection by design:** `sql` is a literal text property and never an expression. Values go in only through
  the `parameters` Dictionary as named parameters (`@name`).
- **Passwords:** a connection string with a password is refused when the plugin loads; the password is the
  activity's secret property.
- **Read-only connections:** a change through one is impossible. SQLite opens read-only, PostgreSQL runs a read-only
  session, and on SQL Server every statement is rolled back.

SQLite tests always run. SQL Server and PostgreSQL run against real servers (`MYRPA_TEST_SQLSERVER`,
`MYRPA_TEST_POSTGRES`, as in the CI `Integration services` job); otherwise they are reported as skipped.

## 5d. SFTP (`MyRPA.Sftp`, 7.1)

Servers are named in the plugin configuration (`server.NAME.host`, `.port`, `.hostKey`, `.username`, `.privateKeyPath`,
`.remoteRoot`), with `fileRoot`, `maxFileBytes` (500 MB) and `timeoutMs`. Details:
[plugins/MyRPA.Sftp/README.md](../../plugins/MyRPA.Sftp/README.md).

- **Host keys are pinned:** a server without a `hostKey` fingerprint is refused when the plugin loads. Any other key is
  refused when connecting (`HostKeyMismatch`, naming the presented key).
- **Remote paths** stay inside the server's `remoteRoot`. An escape through `..` is refused before connecting. Local
  paths go through the file policy, and downloads appear only once complete.
- **Passwords and key passphrases** are secret properties. Uploads never replace a remote file unless asked: the
  server refuses even a file that appears during the upload.

Offline checks always run; transfers run against a real server when `MYRPA_TEST_SFTP` is set (the CI
`Integration services` job), otherwise they are reported as skipped.

## 5e. Scripting (`MyRPA.Scripting`, ADR-0045)

`Code.JavaScript` and `Code.Python` run a function body that reads `inputs` (a Dictionary) and returns a value. Values
cross as JSON both ways. The code is a literal, multiline text property and never an expression, so data never becomes
code. Details: [plugins/MyRPA.Scripting/README.md](../../plugins/MyRPA.Scripting/README.md).

- **JavaScript is a sandbox (Jint):**
  - no .NET, files, network, processes, `eval` or `new Function`;
  - limits on time, statements, memory and recursion.
- **Python is opt-in and not a sandbox:**
  - it runs only when the operator sets `pythonPath`;
  - it uses the installed interpreter in a separate process (`python -I`), with a timeout that kills it;
  - the script has the robot account's rights.

The catalog's `multiline` flag (ADR-0045) also marks the database `sql` property.

## 6. Error types

| errorType | Raised by |
|---|---|
| `InvalidInput` | any activity: a property of the wrong kind (the message names the property, never its value) |
| `InvalidPath`, `FileAccessDenied`, `FileNotFound`, `FileAlreadyExists`, `FileTooLarge`, `TooManyItems`, `FileIoError` | Files and Excel plugins, `Http.Download`/`Http.Upload` |
| `InvalidCsv`, `InvalidJson`, `InvalidXml`, `InvalidArchive` | Files plugin (`InvalidJson` also `Core.Json.Parse` and `Http.Request`) |
| `InvalidWorkbook`, `SheetNotFound` | Excel plugin |
| `InvalidDocument`, `EncryptedDocument` | Documents plugin |
| `ScriptSyntax`, `ScriptError`, `ScriptLimit`, `InvalidResult`, `PythonNotConfigured` | Scripting plugin (with `Timeout`) |
| `ServerNotFound`, `HostKeyMismatch`, `SftpConnection`, `SftpAuthentication`, `RemotePathDenied`, `RemoteFileNotFound`, `RemoteFileExists`, `RemotePermissionDenied` | SFTP plugin (with `Timeout`) |
| `ConnectionNotFound`, `DatabaseConnection`, `DatabaseError`, `ReadOnlyConnection` | Database plugin (with `Timeout`, `TooManyItems`) |
| `HostNotConfigured`, `EmailConnection`, `EmailAuthentication`, `EmailRejected`, `FolderNotFound`, `MessageNotFound`, `MessageTooLarge` | Email plugin (with `Timeout`) |
| `InvalidUrl`, `HostNotAllowed`, `HttpStatus`, `HttpConnection`, `Timeout`, `ResponseTooLarge`, `TooManyRedirects`, `RedirectNotAllowed` | HTTP plugin |
| `InvalidPattern`, `Timeout`, `InvalidFormat`, `InvalidTimeZone` | text and date built-ins (`Timeout` also `File.WaitFor`) |

A `Core.TryCatch` sees them as `err.errorType`.

## 7. Example: the order report

[`samples/plugins/enterprise-order-report.json`](../../samples/plugins/enterprise-order-report.json) runs API → files →
Excel → browser → API. It gets orders with a Bearer token, keeps them as JSON and CSV, writes and re-reads an Excel
report of the open ones, types the count into a web portal, reads its confirmation, and posts a summary back.
`MyRPA.Integration.Tests.EnterpriseSampleTests` runs it through the CLI against a local API and portal. To run it
yourself, see [samples/plugins/README.md](../../samples/plugins/README.md).

Not in Phase 7: Windows desktop automation, Run Python / Run JavaScript, Word and Outlook, FTP/SFTP, a credential store
(Phase 11), AI, MCP and orchestration.
