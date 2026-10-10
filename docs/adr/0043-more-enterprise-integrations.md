# ADR-0043: More enterprise integrations (Phase 7.1): mail, databases, SFTP, documents, HTTP extras

- Status: **Accepted** (owner, 2026-10-09: "Go with your recommendations, and write ADR" for the Phase 7.1 plan and
  its decisions). Implemented 2026-10-10. The plan's §8 records the refinements: servers named in the plugin
  configuration, pinned SFTP host keys, fail-closed file roots, and the PdfPig package id.
- Phase: 7.1 (an extension of Phase 7). Plan: [enterprise-activities-7-1-plan.md](../architecture/enterprise-activities-7-1-plan.md).
- Builds on: ADR-0042 (plugins for side effects, catalog 1.2, secret properties, `FileRootPolicy`), ADR-0009
  (expressions), ADR-0040 (debugger), ADR-0014/0015/0019 (plugins, trust, configuration).

## Context
Phase 7 delivered files, HTTP, Excel and data built-ins. Business processes also need what neither API automation
(`Http.Request`, later an OpenAPI connector) nor MCP (Phase 9) covers well:
- **Non-HTTP protocols:** mail (SMTP/IMAP), databases, SFTP.
- **Local documents:** PDF text, ZIP archives.
- **HTTP features real APIs need:** retry, file download and upload, chat webhooks.

Phase 7 also left three known gaps:
- The debugger shows secret values to the person running the workflow.
- A secret given with `--arg` is visible in the process list.
- A regular-expression backslash must be written twice in an expression string.

SaaS products reached by OAuth (Microsoft 365, Google, Salesforce) are excluded: they wait for the credential store of
Phase 11.

## Decision

### 1. Gap fixes
- **Debugger masking:** the loader finds, statically, the arguments and variables named by secret properties. The debug
  session reports their values as `••••` (`GET /api/runs/{id}/debug` and the Studio's paused view). The run itself is
  unchanged.
- **CLI:** `--arg-env name=ENV_VAR` and `--arg-file name=path` read an argument from an environment variable or a file
  (trailing line break removed), so a secret is not on the command line.
- **Raw strings (ADR-0009 amendment):** `r'...'` is a string literal without escapes; `''` inside it is one quote.
  The Studio's expression assist highlights and completes it like any string.

### 2. New and extended plugins
Each plugin owns its package (`TechnologyPackageOwners`), declares capabilities and side effects, and uses the
`FileRootPolicy` for every local path.

| Plugin | Activities | Package | Side effects |
|---|---|---|---|
| `MyRPA.Http` (extended) | `Http.Request` retry; `Http.Download`, `Http.Upload`, `Chat.Post` | none | Network, FileSystem |
| `MyRPA.Files` (extended) | `Zip.Create`, `Zip.Extract`, `File.Hash`, `File.WaitFor` | none (BCL) | FileSystem |
| `MyRPA.Spreadsheet` (extended) | `Excel.AppendRows`, `Excel.ClearRange` | DocumentFormat.OpenXml | FileSystem |
| `MyRPA.Documents` (new) | `Pdf.ReadText`, `Pdf.GetInfo` | PdfPig (by UglyToad; the NuGet id is `PdfPig`, not the unofficial `UglyToad.PdfPig`) | FileSystem |
| `MyRPA.Email` (new) | `Email.Send`, `Email.Read`, `Email.SaveAttachments`, `Email.MarkRead`, `Email.Move` | MailKit | Network, FileSystem |
| `MyRPA.Database` (new) | `Db.Query`, `Db.Execute`, `Db.Scalar` | Microsoft.Data.Sqlite, Microsoft.Data.SqlClient, Npgsql | Network, FileSystem |
| `MyRPA.Sftp` (new) | `Sftp.List`, `Sftp.Download`, `Sftp.Upload`, `Sftp.Delete`, `Sftp.Move`, `Sftp.CreateFolder` | SSH.NET | Network, FileSystem |

### 3. Security rules
- **SQL:**
  - The statement is a literal text property, never an expression, so workflow data can never become SQL.
  - Values reach the database only as named parameters (the `parameters` Dictionary).
  - Connections are named in the plugin configuration (provider, server, database, user); the password is a secret
    property; a connection may be `readOnly`.
  - `maxRows` and a command timeout bound every call.
  - Providers in 7.1: SQLite, SQL Server, PostgreSQL. MySQL is not included.
- **SFTP:**
  - The server's host key fingerprint must be pinned in the configuration; an unknown or changed key fails
    (`HostKeyMismatch`) before any login.
  - Hosts are allow-listed. The remote side is confined to an optional `remoteRoot`.
  - Login by password (secret) or private key (a file in the configuration, its passphrase secret).
  - Plain FTP and FTPS are not included.
- **Mail:**
  - TLS is required unless a setting allows plain connections for a local test server.
  - Hosts are allow-listed and message sizes limited.
  - Attachments are saved only through the file policy, never overwriting.
  - Microsoft 365 and Gmail work with app passwords; their OAuth sign-in waits for Phase 11.
- **HTTP:**
  - Retries only on connection errors, 429 and 5xx, honour `Retry-After`, and back off exponentially with a cap.
  - Only idempotent methods are retried unless `retryUnsafe` is set.
  - Downloads stream to the file policy under a size limit.
  - A chat webhook URL is a secret property.
- **Archives:**
  - Extraction refuses entries leaving the destination (zip slip), links, and archives above an entry-count or
    uncompressed-size limit (zip bombs).
- **Everywhere:**
  - Secrets never in logs, events or error messages.
  - Cancellation and timeouts are honoured.
  - No state is shared between concurrent runs.

### 4. Testing
- **Local by default:** everything that can run locally does (files, Excel, PDF, ZIP, HTTP against local servers,
  SQLite, SMTP against an in-process fake).
- **Service containers:** IMAP, SFTP, SQL Server and PostgreSQL need real servers. They run as service containers in a
  new Linux CI job.
- **Elsewhere:** those tests run when their connection setting is given, and otherwise are reported as skipped, never
  as passed.

### 5. Delivery
- **First PR:** slices 1–4 (gap fixes, HTTP extras, files/Excel extras, documents), which need no new servers.
- **Second PR:** slices 5–9 (email, database, SFTP, Studio, the invoice-intake definition of done).

## Alternatives considered
- **Vendor connectors** (Microsoft Graph, Google, Salesforce) now: they need OAuth sign-in and token storage (Phase 11).
  `Http.Request` already reaches API-key services, and an OpenAPI connector is planned after Phase 11.
- **MCP servers for databases, mail or SFTP:** they suit AI agents (Phase 9). For deterministic processes they add a
  process, a trust boundary and vendor-dependent quality; native plugins keep validation, limits and audit in MyRPA.
- **SQL as an expression with escaping:** rejected; escaping is the classic source of injection. A literal statement
  with parameters makes injection impossible rather than unlikely.
- **System.Net.Mail:** it only sends mail (no IMAP), and Microsoft recommends MailKit for new code.
- **FTPS and MySQL:** possible later on request; not needed for the first processes.

## Consequences
- **Four new plugins**, each with a new technology package; the package allow-list and `TechnologyPackageOwners` grow
  accordingly.
- **The SQL rule (literal statement) is a deliberate limit:** a workflow cannot build dynamic SQL. Varying filters use
  parameters (for example `WHERE (@status IS NULL OR status = @status)`).
- **CI gains a Linux job with service containers.** Windows CI runs the local tests only.
- **The expression language gains one literal form**, `r'...'`. Older expressions are unaffected.
- **Not included:**
  - desktop automation, OCR, Word templates, barcode/QR, PGP, SSH commands, LDAP, mainframe terminals and SAP RFC
    (later Phase 7 slices);
  - OAuth-based SaaS and the OpenAPI connector (after Phase 11);
  - queues and triggers (Phase 10);
  - AI (Phase 8), MCP and agents (Phase 9).
