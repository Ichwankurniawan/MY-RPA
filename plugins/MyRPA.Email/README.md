# MyRPA.Email

Email activities (Phase 7.1, [ADR-0043](../../docs/adr/0043-more-enterprise-integrations.md)): send through SMTP, and
read, save attachments, mark read and move through IMAP. A product plugin: it references the Automation SDK and
[MailKit](https://github.com/jstedfast/MailKit) 4.18.1 with MimeKit (MIT), which only this plugin may reference. It runs
in-process and is fully trusted (ADR-0015); its load context is not a sandbox.

## Settings (plugin configuration, ADR-0019)

The servers are named only here, so a workflow cannot send mail through a server of its choosing.

| Setting | Default | Meaning |
|---|---|---|
| `smtpHost`, `smtpPort` | not set, 587 | The server `Email.Send` uses (not set: `HostNotConfigured`) |
| `smtpSecurity` | `StartTls` | `StartTls` (required: a server without it is refused), `SslOnConnect`, or `None` (plain text, only for a trusted local relay) |
| `imapHost`, `imapPort` | not set, 993 | The server the other activities use |
| `imapSecurity` | `SslOnConnect` | `SslOnConnect`, `StartTls` or `None` |
| `fileRoot` | not set | The only folder tree attachments are read from and saved to; not set, attachments are refused (`FileAccessDenied`) |
| `defaultFrom` | not set | The sender when `Email.Send` gives none |
| `maxMessageBytes` | 26214400 (25 MB) | The largest message sent (body and attachments) or read |
| `maxMessages` | 100 | The most messages one `Email.Read` returns |
| `timeoutMs` | 60000 | The timeout of one activity's conversation with the server (also capped by the run's deadline) |

Certificates are always validated by the platform; there is no setting to turn validation off.

## Activities

Every activity takes `username` and `password` (**secret**: an argument or variable, validation `MYRPA1066`). Without
a user name, the server is used without logging in.

| Type | Properties | Outputs |
|---|---|---|
| `Email.Send` | `to`, `cc`, `bcc` (an address, several separated by commas, or a List), `from` (default `defaultFrom`), `subject` (one line), `body`, `html`, `attachments` (paths under `fileRoot`) | `messageId` |
| `Email.Read` | `folder` (default `INBOX`), `unreadOnly` (default true), `fromContains`, `subjectContains`, `since`, `maxMessages` (default 10) | `result`: oldest first, each `{ id, from, fromAddress, to, cc, subject, date, text, html, attachments, size, tooLarge }`; a message larger than `maxMessageBytes` has only `id`, `size` and `tooLarge: true` |
| `Email.SaveAttachments` | `id`, `folder` (under `fileRoot`), `pattern` (such as `*.pdf`), `overwrite` (default false) | `result`: the saved paths |
| `Email.MarkRead` | `ids` (one or a List), `read` (default true) | — |
| `Email.Move` | `ids`, `destination` (an existing folder) | — |

A message id is `folder/uidValidity/uid`. When the server rebuilds a folder (its UIDVALIDITY changes), old ids fail with
`MessageNotFound` instead of naming another message. Reading never marks a message read. Attachment names are cleaned:
folders are dropped, characters a file name cannot hold become `_`, and a repeated name gets ` (2)`. Every target is
checked before anything is written.

| errorType | When |
|---|---|
| `HostNotConfigured` | No `smtpHost` or `imapHost` |
| `EmailConnection` | Unreachable, TLS not offered or failed (certificate), connection dropped |
| `EmailAuthentication` | The user name or password was refused (the message never contains them) |
| `EmailRejected` | The SMTP server refused a sender, recipient or message (its code is in the message) |
| `FolderNotFound`, `MessageNotFound` | A folder or message that does not exist (any more) |
| `MessageTooLarge` | Larger than `maxMessageBytes` |
| `Timeout` | No answer within `timeoutMs` |
| `InvalidInput` | A bad address, a subject with a line break, a malformed id |

Microsoft 365 and Gmail need OAuth or app passwords for SMTP and IMAP; OAuth arrives with the credential work of
Phase 11.

## Tests

`tests/MyRPA.Email.Tests` sends against an in-process fake SMTP server, which covers:
- delivery to every recipient, with bcc hidden;
- the password sent and never logged;
- STARTTLS required, and an untrusted certificate refused before the password is sent;
- rejections and timeouts.

The IMAP tests need a real server: set `MYRPA_TEST_MAIL`, for example to
`smtpHost=127.0.0.1;smtpPort=3025;imapHost=127.0.0.1;imapPort=3143;address=robot@example.test;user=robot@example.test;password=secret`
for GreenMail:

```bash
docker run -d -p 3025:3025 -p 3143:3143 -e GREENMAIL_OPTS="-Dgreenmail.setup.test.smtp -Dgreenmail.setup.test.imap -Dgreenmail.hostname=0.0.0.0 -Dgreenmail.users=robot:secret@example.test -Dgreenmail.users.login=email" greenmail/standalone:2.1.14
```

Without it they are reported as skipped, never as passed. CI runs them in the `Integration services` job.
