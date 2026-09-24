# MyRPA.Browser.Playwright

Browser automation provider plugin: eleven `Browser.*` activities over Microsoft Playwright 1.63.0 (Chromium).
Architecture, activities, selectors, errors and security: [docs/architecture/browser-automation.md](../../docs/architecture/browser-automation.md).
Decisions: [ADR-0017](../../docs/adr/0017-browser-automation-provider.md), [ADR-0016](../../docs/adr/0016-verified-path-based-plugin-loading.md).

## Build and install

```bash
dotnet build plugins/MyRPA.Browser.Playwright
```

```bash
pwsh plugins/MyRPA.Browser.Playwright/bin/Debug/net10.0/playwright.ps1 install chromium
```

The build output directory is the plugin directory. It is built for the current platform only; do not set
`PlaywrightPlatform=all`, which makes it exceed the plugin size limit.

## Use

```bash
dotnet run --project src/MyRPA.Cli -- --plugin plugins/MyRPA.Browser.Playwright/bin/Debug/net10.0 run samples/plugins/browser-demo.json --arg url=https://example.com
```

The plugin runs with full trust inside the host: it starts the Playwright driver (Node.js) and Chromium processes and
uses the network. `AssemblyLoadContext` isolates loading; it is not a security boundary.
