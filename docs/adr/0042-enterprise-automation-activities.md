# ADR-0042: Enterprise automation activities, catalog 1.2 metadata and secret properties

- Status: **Accepted** (owner, 2026-10-09: the Phase 7 brief and its scope answers: files and data formats, HTTP, Excel
  `.xlsx`, text/date/collection built-ins; secret-marked properties; additive catalog 1.2).
- Phase: 7 (Enterprise Automation Activities). Plan: [enterprise-activities-plan.md](../architecture/enterprise-activities-plan.md).
- Builds on: ADR-0008 (secure baseline, credential references in Phase 11), ADR-0013 (SDK and activity contract),
  ADR-0014/0015/0019 (plugins, trust, configuration), ADR-0017 (file policy pattern), ADR-0020 (catalog snapshot).

## Context
MyRPA automates browsers only. Phase 7 adds the activities business processes combine with them: files and data
formats, HTTP APIs, Excel workbooks, and text, date and collection handling. Three things are missing today:
1. **Catalog metadata.** The catalog says how a property is written (its kind) but not what value it expects, its
   default, whether it holds a secret, or what the activity touches (files, network, browser). A Studio, and later an
   AI assistant (Phase 8), cannot tell these apart without reading prose.
2. **Secrets.** Credentials are resolved by a credential provider in Phase 11 (ADR-0008 §5, PRD §11.3). HTTP
   authentication needs a token or password now, without storing it in the workflow.
3. **Where side-effecting activities live.** The engine and built-in libraries must not call the network
   (`HttpClient` is a banned API in `src`) and should stay deterministic.

The PRD's Phase 7 also lists Windows desktop automation (7.1), Run Python (7.6) and Run JavaScript (7.7). The owner's
Phase 7 brief excludes desktop automation, arbitrary code execution and unrestricted JavaScript; they stay out of this
phase (and the `automationid=` selector strategy deferred by ADR-0038 stays deferred with desktop automation).

## Decision

### 1. Where activities live
- **Plugins for side effects**, each declaring its capabilities in its manifest (ADR-0014), configured with the plugin
  configuration file (ADR-0019), loaded like the browser plugin:
  - `MyRPA.Files`: files, folders, CSV, JSON and XML files (`FileSystem`).
  - `MyRPA.Http`: HTTP requests (`Network`).
  - `MyRPA.Spreadsheet`: Excel `.xlsx` (`FileSystem`); `DocumentFormat.OpenXml` is allowed only there
    (`TechnologyPackageOwners`).
- **Built-ins for pure work** (`MyRPA.Activities`): text, date, JSON text and collection activities with no side
  effects. Where a single function call is the natural form, it goes into the expression whitelist (ADR-0009) instead
  of becoming an activity.

### 2. Catalog 1.2 metadata (additive)
New optional, `init`-only members (old plugins and catalog 1.1 snapshots keep working; the SDK becomes 1.2):
- `ActivityPropertyDefinition.ValueType`: the value the property expects (`Any`, `String`, `Int`, `Decimal`,
  `Boolean`, `DateTime`, `List`, `Dictionary`).
- `ActivityPropertyDefinition.DefaultValue`: the default when the property is omitted, as JSON text (documentation
  for tools; the activity applies it).
- `ActivityPropertyDefinition.IsSecret`: the property holds a secret (an Expression property only).
- `ActivityDescriptor.SideEffects`: flags `None`, `FileSystem`, `Network`, `Browser`.

The catalog JSON (`GET /api/activities`, the snapshot) adds `valueType`, `default`, `secret` and `sideEffects` and
reports `catalogVersion` 1.2. The Studio shows them (type hint, default as placeholder, a secret badge, side-effect
labels).

### 3. Secret properties until Phase 11
- A secret property must take its value from a name: an argument or variable supplied at run time. Validation refuses
  a constant (a string, number or other literal) with a new diagnostic, so a password or token is never written into
  a workflow file.
- Activities never log a secret, never put it into an error message, and never include it in events.
- When the Phase 11 credential provider arrives, a credential reference becomes another way to fill these properties;
  workflows written now keep working.
- Known limit: the debugger (ADR-0040) shows the values of variables while paused, to the session that started the
  run only; a variable holding a secret is shown there.

### 4. File access policy (Files and Spreadsheet)
- Every path is resolved against one configured root (`fileRoot`, default: the host's working directory), like the
  browser plugin (ADR-0017). A path that leaves the root, or passes through a symbolic link or junction between the
  root and the file, is refused.
- Nothing is overwritten unless `overwrite` is true; Delete removes files only (never folders).
- Reads are bounded (`maxFileBytes`, `maxRows`) and XML is read without DTDs or external resolution.

### 5. HTTP safety
- `http` and `https` only; TLS certificates are always validated.
- Redirects are followed by the plugin (at most 5); the `Authorization` header is never sent to another host.
- Responses are bounded (`maxResponseBytes`); timeouts and cancellation end the request.
- An optional `allowedHosts` setting limits where requests may go (relevant for server-hosted runs; SSRF).
- Status ≥ 400 fails the activity by default (`failOnErrorStatus`); failures are classified: `HttpStatus`,
  `HttpConnection`, `Timeout`, `ResponseTooLarge`.

## Alternatives considered
- **A credential provider now** (environment variables, a local store): rejected by the owner; it would pull Phase 11
  forward.
- **Descriptions only, no new metadata**: rejected; nothing machine-readable for the Studio or Phase 8.
- **Office COM automation for Excel**: rejected; it needs Office on the machine and does not run on servers.
- **Network and file I/O as built-ins in `src`**: rejected; side effects belong in plugins with declared capabilities,
  and `HttpClient` stays banned in `src`.

## Implementation notes (2026-10-09)

- The file policy is public SDK API (`MyRPA.Sdk.Files.FileRootPolicy`, `FileErrorTypes`), additive to SDK 1.2.
- `Http.Request` takes `headers` as a Dictionary expression; `auth` also has `ApiKey`. A failing status
  (`failOnErrorStatus`) fails before any output is assigned. Further error types: `InvalidUrl`, `HostNotAllowed`,
  `TooManyRedirects`, `RedirectNotAllowed`, `InvalidJson`.
- The Excel plugin adds `maxCells`; `DocumentFormat.OpenXml` 3.5.1 is owned by `MyRPA.Spreadsheet` only.
- The CLI references `MyRPA.Browser.Contracts` so that it can host the browser plugin (a gap left by ADR-0039).

## Consequences
- SDK 1.2 and catalog 1.2: additive; plugins built for 1.0/1.1 load unchanged.
- One new diagnostic code for secret literals; new error types for the plugins.
- One new technology package, `DocumentFormat.OpenXml`, owned by `MyRPA.Spreadsheet`.
- Windows automation, Python and JavaScript remain unimplemented; the PRD's Phase 7 list is narrowed by this ADR.
