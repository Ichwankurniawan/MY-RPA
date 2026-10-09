# Enterprise automation activities (Phase 7)

The activity catalog added in Phase 7 ([ADR-0042](../adr/0042-enterprise-automation-activities.md), plan:
[enterprise-activities-plan.md](enterprise-activities-plan.md)): data built-ins in the engine, and three product
plugins for work with side effects. Every activity is described by catalog 1.2 metadata (value types, defaults,
secret properties, side effects), so the Studio builds its editors from the catalog with no per-activity code.

| Where | Activities | Side effect | Package |
|---|---|---|---|
| Built-ins (`MyRPA.Activities`) | `Core.Text.*`, `Core.Json.*`, `Core.Date.*`, `Core.Collection.*` | none | none |
| `plugins/MyRPA.Files` | `File.*`, `Folder.Create`, `Csv.*`, `Json.ReadFile`/`WriteFile`, `Xml.ReadFile` | FileSystem | none |
| `plugins/MyRPA.Http` | `Http.Request` | Network | none (.NET `HttpClient`) |
| `plugins/MyRPA.Spreadsheet` | `Excel.GetSheets`, `Excel.ReadRange`, `Excel.WriteRange` | FileSystem | `DocumentFormat.OpenXml` 3.5.1 (this plugin only) |

The plugins are loaded like any plugin: `--plugin <directory>` (repeatable) or a plugin configuration file with their
settings (ADR-0019). Plugins run in-process with full trust (ADR-0015); a load context is not a sandbox. The limits
below are what the activities enforce themselves.

## 1. Secret properties

A property marked secret in the catalog (`Http.Request` `token` and `password`) must name an argument or variable; a
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

Settings: `fileRoot`, `maxFileBytes` (50 MB), `maxItems` (10000), `maxRows` (100000). Details:
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

## 4. HTTP (`MyRPA.Http`)

Settings: `allowedHosts`, `maxResponseBytes` (10 MB), `maxRedirects` (5), `defaultTimeoutMs` (30000). Details:
[plugins/MyRPA.Http/README.md](../../plugins/MyRPA.Http/README.md).

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

## 6. Error types

| errorType | Raised by |
|---|---|
| `InvalidInput` | any activity: a property of the wrong kind (the message names the property, never its value) |
| `InvalidPath`, `FileAccessDenied`, `FileNotFound`, `FileAlreadyExists`, `FileTooLarge`, `TooManyItems`, `FileIoError` | Files and Excel plugins |
| `InvalidCsv`, `InvalidJson`, `InvalidXml` | Files plugin (`InvalidJson` also `Core.Json.Parse` and `Http.Request`) |
| `InvalidWorkbook`, `SheetNotFound` | Excel plugin |
| `InvalidUrl`, `HostNotAllowed`, `HttpStatus`, `HttpConnection`, `Timeout`, `ResponseTooLarge`, `TooManyRedirects`, `RedirectNotAllowed` | HTTP plugin |
| `InvalidPattern`, `Timeout`, `InvalidFormat`, `InvalidTimeZone` | text and date built-ins |

A `Core.TryCatch` sees them as `err.errorType`.

## 7. Example: the order report

[`samples/plugins/enterprise-order-report.json`](../../samples/plugins/enterprise-order-report.json) runs API → files →
Excel → browser → API. It gets orders with a Bearer token, keeps them as JSON and CSV, writes and re-reads an Excel
report of the open ones, types the count into a web portal, reads its confirmation, and posts a summary back.
`MyRPA.Integration.Tests.EnterpriseSampleTests` runs it through the CLI against a local API and portal. To run it
yourself, see [samples/plugins/README.md](../../samples/plugins/README.md).

Not in Phase 7: Windows desktop automation, Run Python / Run JavaScript, Word and Outlook, FTP/SFTP, a credential store
(Phase 11), AI, MCP and orchestration.
