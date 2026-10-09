# Phase 7.1 — More enterprise integrations: plan

Status: **approved** by the owner on 2026-10-09 ("Go with your recommendations, and write ADR"); decision record
[ADR-0043](../adr/0043-more-enterprise-integrations.md). Builds on Phase 7
([ADR-0042](../adr/0042-enterprise-automation-activities.md), [enterprise-activities.md](enterprise-activities.md)). Not in scope: AI (Phase 8), MCP and agents (Phase 9), queues and
triggers (Phase 10), OAuth sign-in and a credential store (Phase 11).

## 1. Goal

Cover the integrations that API automation and MCP do not cover well: local documents, non-HTTP protocols (mail,
SFTP, databases), and the HTTP features real APIs need. Close the three known gaps of Phase 7 first. The definition of
done is one realistic business process run end to end (§4, slice 9).

## 2. What is reused (no new contracts)

- Plugins with catalog 1.2 metadata (value types, defaults, secret properties, side effects); SDK 1.2 unchanged
  except additive helpers.
- `MyRPA.Sdk.Files.FileRootPolicy` for every local path (downloads, attachments, archives, extracted files).
- Secret properties (`MYRPA1066`) for every password, token, webhook URL and key passphrase.
- The error-type style of ADR-0042, plugin settings (ADR-0019), technology packages owned by one plugin
  (`TechnologyPackageOwners`).

## 3. Activities

### 3.1 Gap fixes (engine, CLI, Studio)
| Change | Detail |
|---|---|
| Debugger hides secrets | Values of arguments and variables that feed a secret property (found statically by the loader) are shown as `••••` in the paused view and `GET /api/runs/{id}/debug` |
| `--arg-env name=ENV_VAR` | CLI reads an argument from an environment variable, so a secret is not in the process list; also `--arg-file name=path` |
| Raw strings in expressions | `r'\d+'`: no escapes inside (ADR-0009 amendment); Studio expression assist and highlighting follow |

### 3.2 HTTP plugin extras (`MyRPA.Http`)
| Activity / property | Detail |
|---|---|
| `Http.Request` `retries`, `retryDelayMs` | Retries on connection errors, 429 and 5xx, honours `Retry-After`, exponential backoff with a cap; only GET, HEAD, PUT, DELETE unless `retryUnsafe` |
| `Http.Download` | Streams a response to a file (new `fileRoot` setting, FileRootPolicy, `overwrite`, `maxDownloadBytes`) |
| `Http.Upload` | Multipart form: files from the root plus text fields |
| `Chat.Post` | Teams, Slack, Discord or generic webhook: a message (and a title/colour where the platform has one); the webhook URL is a secret property |

### 3.3 Files and Excel extras
| Activity | Plugin | Detail |
|---|---|---|
| `Zip.Create`, `Zip.Extract` | MyRPA.Files | BCL `System.IO.Compression`; extraction refuses paths leaving the destination (zip slip), and limits entries and total uncompressed size (zip bombs) |
| `File.Hash` | MyRPA.Files | SHA-256 (or SHA-512) of a file |
| `File.WaitFor` | MyRPA.Files | Waits until a file exists (optionally stable in size) or the timeout passes; polls on the injected clock |
| `Excel.AppendRows`, `Excel.ClearRange` | MyRPA.Spreadsheet | Append after the last used row; clear values in a range |

### 3.4 Documents plugin (new: `MyRPA.Documents`)
| Activity | Detail |
|---|---|
| `Pdf.ReadText` | Text of all pages or a page range (package: PdfPig) |
| `Pdf.GetInfo` | Page count, title, author |

Scanned PDFs (images) give no text; OCR is a later slice.

### 3.5 Email plugin (new: `MyRPA.Email`)
| Activity | Detail |
|---|---|
| `Email.Send` | SMTP: to/cc/bcc, subject, text or HTML body, attachments from the root; TLS required unless the setting allows plain |
| `Email.Read` | IMAP: a folder, filters (unread, from, subject contains, since), `maxMessages`; returns messages (from, to, subject, date, text, attachment names, id) |
| `Email.SaveAttachments` | Saves a message's attachments into the root (name filter such as `*.pdf`, no overwrite) |
| `Email.MarkRead`, `Email.Move` | By message id |

Settings: `smtpHost`, `imapHost`, ports, `allowedHosts`, `maxMessageBytes`; `password` is a secret property.
Known limit: Microsoft 365 and Gmail need OAuth or app passwords for IMAP/SMTP; OAuth arrives with Phase 11.

### 3.6 Database plugin (new: `MyRPA.Database`)
| Activity | Detail |
|---|---|
| `Db.Query` | Rows as a table (List of Dictionaries), `maxRows` |
| `Db.Execute` | INSERT/UPDATE/DELETE; returns rows affected |
| `Db.Scalar` | One value |

Rules:
- The SQL is a **literal text property**, never an expression, so workflow data can never become SQL. Values go in
  only through the `parameters` Dictionary (named parameters).
- Connections are named in the plugin configuration (provider, server, database, user). The password is a secret
  property; a connection can be marked `readOnly`.
- Settings include a command timeout and `maxRows`.

Providers: SQLite, SQL Server, PostgreSQL (MySQL on request).

### 3.7 SFTP plugin (new: `MyRPA.Sftp`)
| Activity | Detail |
|---|---|
| `Sftp.List`, `Sftp.Download`, `Sftp.Upload`, `Sftp.Delete`, `Sftp.Move`, `Sftp.CreateFolder` | Local side through FileRootPolicy; remote side confined to an optional `remoteRoot` |

Security and settings:
- The **host key fingerprint must be pinned** in the configuration; an unknown or changed key fails (`HostKeyMismatch`).
- Login by password (secret property) or by private key (path in settings, passphrase secret).
- Settings: `allowedHosts`, `maxFileBytes`.

## 4. Slices

| Slice | What | Done when |
|---|---|---|
| 1 Gaps | Debugger masking, `--arg-env`/`--arg-file`, raw strings | Engine, server, CLI and Vitest tests; smoke step for the masked debugger |
| 2 HTTP | Retry, Download, Upload, `Chat.Post` | Local-server tests: retry and Retry-After, download limits and file policy, multipart, webhook payloads |
| 3 Files/Excel | Zip, Hash, WaitFor, AppendRows, ClearRange | Zip slip and zip bomb tests, waiting on a fake clock, Excel append on generated workbooks |
| 4 Documents | `MyRPA.Documents` with PDF text | Generated PDFs: text, page ranges, encrypted or broken files |
| 5 Email | `MyRPA.Email` | Tests against an in-process SMTP fake; IMAP against a mail server container (see §5) |
| 6 Database | `MyRPA.Database` | SQLite always; SQL Server and PostgreSQL in containers (§5); injection impossible by design (test: data with quotes stays data) |
| 7 SFTP | `MyRPA.Sftp` | SFTP server container: host key pinning, transfers, remote root, limits |
| 8 Studio | Catalog-driven only (no per-activity UI); raw-string highlighting; docs and samples | Vitest, smoke, a11y |
| 9 Definition of done | **Invoice intake** sample (below) | Integration test through the CLI; full build and tests |

**Invoice intake** (slice 9):
1. Read unread mail from IMAP and save the PDF attachments.
2. Read each PDF's text and extract the invoice number and amount with `Core.Text.Match`.
3. Insert the invoice into the database (parameters only).
4. Append it to the Excel register.
5. Zip the day's PDFs and upload the archive by SFTP.
6. Post a summary to a chat webhook; mark the mails read.

## 5. Testing approach

- Everything that can run locally does: Files, Excel, PDF, ZIP, HTTP (local servers), SQLite, SMTP (an in-process
  fake).
- IMAP, SFTP, SQL Server and PostgreSQL need real servers. They run as service containers in a new Linux CI job
  (`Integration services`). Locally they run only when their connection setting is given (documented), and are
  otherwise reported as skipped — never counted as passed.
- Every plugin keeps the Phase 7 bar:
  - traversal and link refusal;
  - limits;
  - secrets never in logs or errors (captured-log test);
  - cancellation and timeouts;
  - concurrent runs.

## 6. Decisions (taken by the owner on 2026-10-09: the recommendations)

Approved:
- the packages in point 1;
- SQLite, SQL Server and PostgreSQL (no MySQL);
- the Linux CI job with service containers;
- no FTPS;
- slices 1–4 delivered first.

The questions as they were asked:

1. **New packages** (each owned by one plugin, MIT/Apache/PostgreSQL licences):
   - MailKit for SMTP/IMAP;
   - SSH.NET for SFTP;
   - UglyToad.PdfPig for PDF;
   - Microsoft.Data.SqlClient, Npgsql and Microsoft.Data.Sqlite for databases.
2. **Database providers in 7.1:** SQLite, SQL Server and PostgreSQL. Add MySQL?
3. **CI service containers** for IMAP, SFTP, SQL Server and PostgreSQL (Linux job only).
4. **FTPS (FTP over TLS) in addition to SFTP?** It needs one more package (FluentFTP). Plain FTP stays excluded.
5. **Order:** as in §4. Slices 1–4 need no new servers and could be delivered first as a smaller PR.

## 7. Not in 7.1

- **Later in Phase 7:** Windows desktop automation, OCR, Word templates, barcode/QR, PGP, SSH commands, LDAP,
  mainframe terminals and SAP RFC.
- **API automation after Phase 11:** Microsoft 365/Google/Salesforce via OAuth, and the OpenAPI connector.
- **Phase 10:** queues and triggers.
