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

[`phase7-plugins.json`](phase7-plugins.json) loads the Debug builds of the three plugins; their `fileRoot` is
`samples/plugins/work` (git-ignored), so run from the repository root:

```bash
dotnet build plugins/MyRPA.Files && dotnet build plugins/MyRPA.Spreadsheet && dotnet build plugins/MyRPA.Http
dotnet run --project src/MyRPA.Cli -- run samples/data-builtins.json
dotnet run --project src/MyRPA.Cli -- --plugin-config samples/plugins/phase7-plugins.json run samples/plugins/files-excel-demo.json
dotnet run --project src/MyRPA.Cli -- --plugin-config samples/plugins/phase7-plugins.json run samples/plugins/http-demo.json
```

In the Studio: `dotnet run --project src/MyRPA.Server -- --project samples --plugin-config samples/plugins/phase7-plugins.json --web web/studio/dist --port 0`.

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

Plugins run with full trust inside MyRPA: `AssemblyLoadContext` isolates loading, it is not a security boundary.
See [docs/architecture/plugin-system.md](../../docs/architecture/plugin-system.md).
