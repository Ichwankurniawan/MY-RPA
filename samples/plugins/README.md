# Sample plugin

`MyRPA.Samples.DemoPlugin` is a complete, deterministic plugin that shows the Automation SDK without touching a real
technology:

| Contribution | What it shows |
|---|---|
| `DemoPlugin` (`IPlugin`) | Manifest-named entry point, settings in `Initialize` (`echoPrefix`), explicit `Register` |
| `Demo.Echo` | An activity with an injected plugin-owned instance (`DemoOptions`) and correlated logging |
| `Demo.Text` provider (`IDemoTextProvider : IAutomationProvider, ISelectorResolver`) | A technology interface plus its implementation, registered with plugin lifetime |
| `Demo.GetField` | The provider pattern end to end: `Selector` → `ISelectorResolver` → `SelectorMatch` → `IAutomationElement`; a missing field fails with `errorType` `ElementNotFound` |

Run it:

```bash
dotnet build samples/plugins/MyRPA.Samples.DemoPlugin
```

```bash
dotnet run --project src/MyRPA.Cli -- --plugin samples/plugins/MyRPA.Samples.DemoPlugin/bin/Debug/net10.0 run samples/plugins/demo-plugin.json --arg customer=Grace
```

The build output directory is the plugin directory: `myrpa-plugin.json`, the DLL and its `.deps.json`, and nothing
from the host. Use `myrpa --plugin <dir> plugins` to see the SHA-256 digest you would pin.

## Phase 7 tours

| Workflow | Shows | Needs |
|---|---|---|
| [`../data-builtins.json`](../data-builtins.json) | Text split/join/regex, dates with a time zone, table filter/sort/find/merge, JSON round trip | nothing (built-ins) |
| [`files-excel-demo.json`](files-excel-demo.json) | Text, CSV (formula guard), JSON and XML files, copy/list/delete, an Excel workbook written and read back, `FileAccessDenied` and `FileAlreadyExists` caught | MyRPA.Files, MyRPA.Spreadsheet |
| [`http-demo.json`](http-demo.json) | GET with Dictionary headers, JSON response as a table, POST with a JSON body, a 404 caught (`HttpStatus`) and kept (`failOnErrorStatus` false) | MyRPA.Http, Internet (JSONPlaceholder, fake data) |
| [`script-demo.json`](script-demo.json) (ADR-0045) | Confined JavaScript (no files, network or .NET) totals invoice lines per customer and builds a summary; a thrown script error caught as `ScriptError` | MyRPA.Scripting (for Python: set its `pythonPath`) |
| [`local-71-demo.json`](local-71-demo.json) (7.1) | A file hashed and waited for, a folder zipped and extracted, invoices stored and queried in SQLite with parameters only, an Excel register appended to and partly cleared | MyRPA.Files, MyRPA.Database (connection `demo`), MyRPA.Spreadsheet |

[`all-plugins.json`](all-plugins.json) loads the Debug builds of every sample plugin (demo, browser, files, Excel, HTTP, documents, database with the SQLite connection `demo`, email and SFTP without servers, scripting without `pythonPath`, so their activities appear in the catalog); their `fileRoot` is
`samples/plugins/work` (git-ignored), so run from the repository root:

```bash
dotnet build plugins/MyRPA.Files && dotnet build plugins/MyRPA.Spreadsheet && dotnet build plugins/MyRPA.Http && dotnet build plugins/MyRPA.Documents && dotnet build plugins/MyRPA.Database && dotnet build plugins/MyRPA.Email && dotnet build plugins/MyRPA.Sftp && dotnet build plugins/MyRPA.Scripting
dotnet run --project src/MyRPA.Cli -- run samples/data-builtins.json
dotnet run --project src/MyRPA.Cli -- --plugin-config samples/plugins/all-plugins.json run samples/plugins/files-excel-demo.json
dotnet run --project src/MyRPA.Cli -- --plugin-config samples/plugins/all-plugins.json run samples/plugins/http-demo.json
```

In the Studio: `dotnet run --project src/MyRPA.Server -- --project samples --plugin-config samples/plugins/all-plugins.json --web web/studio/dist --port 0`.

## Enterprise sample: order report (Phase 7)

[`enterprise-order-report.json`](enterprise-order-report.json) uses the product plugins: `Http.Request` (Bearer token
from a secret property), `Json.WriteFile`, `Csv.Write`, `Core.Collection.Filter`, `Excel.WriteRange`/`ReadRange`, the
browser activities and a final POST. Build the plugins, write a plugin configuration file such as:

```json
{ "pluginConfigVersion": "1.0",
  "plugins": [
    { "directory": "plugins/MyRPA.Files/bin/Debug/net10.0", "settings": { "fileRoot": "work" } },
    { "directory": "plugins/MyRPA.Http/bin/Debug/net10.0", "settings": { "allowedHosts": "api.example.test" } },
    { "directory": "plugins/MyRPA.Spreadsheet/bin/Debug/net10.0", "settings": { "fileRoot": "work" } },
    { "directory": "plugins/MyRPA.Browser.Playwright/bin/Debug/net10.0", "settings": { "fileRoot": "work" } } ] }
```

then run it against your API and portal:

```bash
dotnet run --project src/MyRPA.Cli -- --plugin-config plugins.json run samples/plugins/enterprise-order-report.json --arg apiBase=https://api.example.test/api --arg portalUrl=https://portal.example.test/ --arg apiToken=...
```

The integration test `EnterpriseSampleTests` runs exactly this against a local API and portal. The API must answer
`GET {apiBase}/orders` with a JSON list of orders (`status` "open" or not) and accept `POST {apiBase}/reports`; the
portal needs an input `#count`, a button `#submit` and shows `#confirmation`.

## Enterprise sample: invoice intake (Phase 7.1)

[`invoice-intake.json`](invoice-intake.json) is the Phase 7.1 definition of done (ADR-0043). It:
1. reads unread invoice mails (`Email.Read`) and saves each one's PDF attachments into its own folder;
2. reads each PDF's text (`Pdf.ReadText`) and extracts the invoice number and amount with `Core.Text.Match` (raw-string
   patterns);
3. stores the invoice in SQLite with parameters only (`Db.Execute`) and appends it to an Excel register
   (`Excel.AppendRows`);
4. marks the mail read;
5. zips the PDFs (`Zip.Create`), uploads the archive by SFTP (`Sftp.Upload`, pinned host key) and posts a summary to a
   chat webhook (`Chat.Post`).

It needs a plugin configuration with:
- the email plugin's `smtpHost`/`imapHost`;
- a database connection `invoices`;
- an SFTP server `bank` with its `hostKey`;
- the documents, Excel and files plugins over the same `fileRoot`.

Then run:

```bash
dotnet run --project src/MyRPA.Cli -- --plugin-config plugins.json run samples/plugins/invoice-intake.json --arg-env mailUser=MAIL_USER --arg-env mailPassword=MAIL_PASSWORD --arg-env sftpPassword=SFTP_PASSWORD --arg-env chatWebhook=CHAT_WEBHOOK
```

`InvoiceIntakeTests` runs exactly this against GreenMail, an SFTP server, SQLite and a local webhook; CI runs it in the
`Integration services` job.

Plugins run with full trust inside MyRPA: `AssemblyLoadContext` isolates loading, it is not a security boundary.
See [docs/architecture/plugin-system.md](../../docs/architecture/plugin-system.md).
