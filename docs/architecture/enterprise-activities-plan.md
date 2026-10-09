# Phase 7 — Enterprise Automation Activities: plan

Status: **approved** by the owner on 2026-10-09 (the Phase 7 brief and its scope answers). Decision record:
[ADR-0042](../adr/0042-enterprise-automation-activities.md). Phase 8 must not start without the owner's authorization.

## 1. Inspection (2026-10-09)

**Implemented and tested before Phase 7:**
- Phases 1–6: engine, workflow format 1.1, SDK 1.1, plugins, browser automation (Playwright), Web Studio, selectors
  and recorder.
- Follow-up slices: the Studio card designer, graph workflows, the debugger (ADR-0040) and expression assist
  (ADR-0041).
- CI: green on `main`.

**Stable contracts reused as they are:**
- Activities: `IActivity` and `IActivityContext` (SDK 1.1); outputs through assignment-target properties
  (`SetValue`); failures as `ActivityFailedException` / `AutomationException` with an error type.
- Workflows: the run deadline (`context.Deadline`) and cancellation; the validation pipeline (ADR-0011).
- Plugins: the plugin host, manifest capabilities (ADR-0014) and configuration file (ADR-0019).
- Catalog: the activity catalog snapshot (ADR-0020); the Studio builds its editors from it with no per-activity code.

**Gaps closed in this phase (ADR-0042):**
- Catalog metadata: value types, defaults, secret properties and side effects.
- A safe way to give HTTP a secret before the Phase 11 credential provider.

**Out of this phase:**
- Windows desktop automation (PRD 7.1), with the `automationid=` selector strategy.
- Run Python and Run JavaScript (PRD 7.6, 7.7).
- Word and Outlook (PRD 7.5 says "eventually").
- File transfer (FTP/SFTP): no demonstrated need; it would add a protocol dependency.
- Credential storage (Phase 11), AI (Phase 8), MCP and agents (Phase 9), orchestration (Phase 10).

## 2. Activities

Paths are relative to the plugin's `fileRoot` (or absolute inside it). Outputs are assignment targets (`to`,
`result`…), optional unless noted. Values are the workflow's canonical values; a table is a List of Dictionaries.

### MyRPA.Files plugin (side effect: FileSystem)
| Type | Inputs | Outputs | Notes |
|---|---|---|---|
| `File.Exists` | `path` | `result` (Boolean) | Files and folders |
| `File.List` | `folder`, `pattern` (default `*`), `recursive` (default false) | `result` (List of paths, relative to the root) | Bounded by `maxItems` |
| `File.ReadText` | `path`, `encoding` (utf-8 default) | `result` (String) | Bounded by `maxFileBytes` |
| `File.WriteText` | `path`, `text`, `overwrite` (default false), `append` | — | Creates the folder |
| `File.Copy` / `File.Move` | `source`, `destination`, `overwrite` (default false) | — | Files only |
| `File.Delete` | `path`, `missingOk` (default false) | — | Files only, never folders |
| `Folder.Create` | `path` | — | Idempotent |
| `Csv.Read` | `path`, `hasHeader` (default true), `delimiter` (default `,`) | `result` (List of Dictionaries, or of Lists without a header) | RFC 4180 quoting; `maxRows` |
| `Csv.Write` | `path`, `rows`, `columns` (optional order), `delimiter`, `overwrite` | — | Formula-injection guard: cells starting with `= + - @` get a leading `'` when `protectFormulas` (default true) |
| `Json.ReadFile` / `Json.WriteFile` | `path` (+ `value`, `indented`, `overwrite`) | `result` | Depth limit 64 |
| `Xml.ReadFile` | `path` | `result` (Dictionary: `name`, `attributes`, `text`, `children`) | No DTD, no external resolution |

### MyRPA.Http plugin (side effect: Network)
| Type | Inputs | Outputs |
|---|---|---|
| `Http.Request` | `method` (GET, POST, PUT, PATCH, DELETE, HEAD), `url`, `headers` (map), `body` (a String is sent as text; any other value as JSON), `auth` (None, Bearer, Basic), `token` (**secret**), `username`, `password` (**secret**), `timeoutMs` (default 30000, capped by the run deadline), `failOnErrorStatus` (default true), `parseJson` (default true) | `status` (Int), `responseHeaders` (Dictionary), `responseBody` (parsed JSON or String) |

Settings: `allowedHosts`, `maxResponseBytes` (default 10 MB), `maxRedirects` (default 5).

### MyRPA.Spreadsheet plugin (side effect: FileSystem; `DocumentFormat.OpenXml`)
| Type | Inputs | Outputs | Notes |
|---|---|---|---|
| `Excel.GetSheets` | `path` | `result` (List of names) | |
| `Excel.ReadRange` | `path`, `sheet`, `range` (e.g. `A1:D100`, default the used range), `hasHeader` (default true) | `result` (List of Dictionaries or Lists) | Streams the sheet; `maxRows` |
| `Excel.WriteRange` | `path`, `sheet`, `startCell` (default `A1`), `rows`, `columns`, `writeHeader` (default true), `createFile` (default true) | — | Creates the workbook or sheet when missing; overwrites only the written cells |

Numbers, Booleans, text and dates are read as the matching workflow values; formulas give their cached value.

### Built-ins (no side effects)
| Type | Inputs | Outputs |
|---|---|---|
| `Text.Split` | `text`, `separator`, `removeEmpty` (default false) | `result` (List) |
| `Text.Join` | `items`, `separator` | `result` (String) |
| `Text.Match` | `text`, `pattern` (.NET regex, 1 s timeout), `all` (default false) | `result` (the match, its groups as a Dictionary, or a List of them) |
| `Text.Replace` (regex) | `text`, `pattern`, `replacement` | `result` |
| `Json.Parse` / `Json.Serialize` | `text` / `value`, `indented` | `result` |
| `Date.Parse` | `text`, `format` (optional exact format), `timeZone` (IANA id, default UTC) | `result` (DateTime) |
| `Date.Format` | `value`, `format`, `timeZone` | `result` (String) |
| `Date.Add` | `value`, `days`, `hours`, `minutes`, `seconds`, `months` | `result` |
| `Date.Difference` | `from`, `to`, `unit` (Days, Hours, Minutes, Seconds) | `result` (Decimal) |
| `Collection.Sort` | `items`, `key` (for dictionaries), `descending` | `result` |
| `Collection.Filter` | `items`, `key`, `operator` (Equals, NotEquals, Contains, GreaterThan, LessThan, IsNull, IsNotNull), `value` | `result` |
| `Collection.Find` | same as Filter | `result` (first match or null), `index` (Int, −1 when none) |
| `Collection.Merge` | `first`, `second`, `distinct` (default false) | `result` |

Simple one-call operations stay as expression functions (they already exist: `len`, `contains`, `replace`, `trim`,
`upper`, `lower`, `substring`, `now`, `append`, `keys`); there are no duplicate activities for them.

## 3. Slices

| Slice | What | Done when |
|---|---|---|
| P7-1 Metadata | Catalog 1.2 (`valueType`, `default`, `secret`, `sideEffects`), SDK 1.2, the secret-literal diagnostic, Studio display | Workflow, server and Vitest tests; snapshot round trip; old 1.1 snapshots still load |
| P7-2 Built-ins | Text, Json, Date, Collection activities | Activities tests through the real engine; validation; catalog |
| P7-3 Files | `MyRPA.Files` plugin with its file policy | Plugin tests: every activity, traversal, symbolic links and junctions, overwrite refusal, limits, CSV quoting and formula guard, hostile XML, concurrent runs |
| P7-4 HTTP | `MyRPA.Http` plugin | Tests against a local server: methods, JSON, headers, auth, redirects (no credential leak), size limit, timeout, cancellation, status mapping, allowed hosts, no secret in logs or errors |
| P7-5 Excel | `MyRPA.Spreadsheet` plugin | Generated workbooks: read, write, create, ranges, types, large sheet streaming, limits |
| P7-6 Definition of done | A sample business process: API → File → Excel → Browser → API against local servers; docs; Phase Completion Report | Integration test runs it through the CLI; smoke in the Studio; full build and tests |

## 4. Rules
- SDK contract unchanged except the additive 1.2 metadata; no new activity interfaces.
- Activities: cancellation honoured, deadlines respected, resources disposed, no static mutable state, no secrets in
  logs, errors or events.
- Every plugin declares its capabilities and side effects; packages stay in their owner plugin.
- No desktop automation, code execution, AI, MCP or orchestration.
