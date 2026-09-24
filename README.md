# MyRPA

A modern, extensible RPA platform in C#/.NET 10, inspired by OpenRPA and redesigned from the
[Phase 0 research](docs/research/openrpa-analysis.md). Product requirements: [MyRPA-PRD.md](MyRPA-PRD.md).

**Status:** Phase 4 — Browser Automation. Versioned JSON workflows are validated and executed by a deterministic
async engine with 12 control-flow activities, via the `myrpa` CLI. Plugins add activities and automation providers
through the Automation SDK and are loaded into isolated `AssemblyLoadContext`s. The first real provider is the
Playwright browser plugin (Chromium, 11 `Browser.*` activities). No UI (Studio is Phase 5) and no desktop automation yet.

## Repository layout

```text
MyRPA.sln
src/
  MyRPA.Core          platform-neutral contracts (no dependencies)
  MyRPA.Workflow      workflow model, JSON format, validation, expressions, execution contracts
  MyRPA.Activities    activity catalog + built-in Core.* activities
  MyRPA.Runtime       workflow engine, execution scopes, logging/tracing
  MyRPA.Storage       workflow files and InvokeWorkflow resolution (no database)
  MyRPA.Sdk           Automation SDK: plugin contract, provider/element/selector abstractions
  MyRPA.Plugins       plugin host: manifests, discovery, trust checks, AssemblyLoadContext loading, lifecycle
  MyRPA.Cli           `myrpa` command-line host (composition root)
plugins/
  MyRPA.Browser.Playwright  browser automation provider (Playwright; the only Playwright reference)
tests/
  MyRPA.*.Tests       unit tests per library (Sdk: the activity contract; Plugins: loading and lifecycle)
  MyRPA.Integration.Tests   CLI in-process and as a real process
  MyRPA.Architecture.Tests  dependency, platform and security rules
  fixtures/           plugins used by the plugin tests
samples/              example workflows
  plugins/            sample plugin (MyRPA.Samples.DemoPlugin) and a workflow that uses it
docs/                 architecture, ADRs, research
reference/            read-only OpenRPA clone for research (git-ignored)
```

Architecture: [docs/architecture/overview.md](docs/architecture/overview.md) ·
Engine: [docs/architecture/execution-model.md](docs/architecture/execution-model.md) ·
Workflow format: [docs/architecture/workflow-format.md](docs/architecture/workflow-format.md) ·
SDK: [docs/architecture/automation-sdk.md](docs/architecture/automation-sdk.md) ·
Plugins: [docs/architecture/plugin-system.md](docs/architecture/plugin-system.md) ·
Browser: [docs/architecture/browser-automation.md](docs/architecture/browser-automation.md).

## Development setup

- **.NET SDK 10.0.100 or later** (`global.json` rolls forward within 10.0). Check with `dotnet --list-sdks`.
- Git. Any IDE with .NET 10 support (Visual Studio 2026, Rider, VS Code + C# Dev Kit).

No databases or services are needed. The browser plugin tests need Playwright's Chromium, installed once per machine
after the first build:

```bash
pwsh plugins/MyRPA.Browser.Playwright/bin/Debug/net10.0/playwright.ps1 install chromium
```

## Build and test

```bash
dotnet build MyRPA.sln
```

```bash
dotnet test --solution MyRPA.sln
```

Warnings are errors; tests use xUnit v3 on Microsoft.Testing.Platform (opted in via `global.json`).

## Use the CLI

```bash
dotnet run --project src/MyRPA.Cli -- validate samples/control-flow.json
```

```bash
dotnet run --project src/MyRPA.Cli -- run samples/hello-world.json --arg userName=Ada
```

```text
myrpa validate <workflow.json>
myrpa run <workflow.json> [--arg name=value]... [--timeout seconds] [--correlation-id id]
myrpa plugins | info | help | version
global options (anywhere): --verbose, --plugin <directory> (repeatable)
```

`run` prints the execution result (status, ids, outputs, error) as JSON on stdout; workflow `Core.Log` messages and
other logs go to stderr. Exit codes: 0 success, 1 failed, 2 usage, 3 invalid workflow, 4 timed out,
5 plugin failed to load, 130 cancelled.

### Plugins

```bash
dotnet build samples/plugins/MyRPA.Samples.DemoPlugin
```

```bash
dotnet run --project src/MyRPA.Cli -- --plugin samples/plugins/MyRPA.Samples.DemoPlugin/bin/Debug/net10.0 run samples/plugins/demo-plugin.json --arg customer=Grace
```

A plugin is a directory with a `myrpa-plugin.json` manifest; only directories you name are loaded. Plugins run with
full trust inside the process — `AssemblyLoadContext` isolates loading, it is not a security boundary. See
[docs/architecture/plugin-system.md](docs/architecture/plugin-system.md).

A minimal workflow:

```json
{
  "schemaVersion": "1.0",
  "id": "hello",
  "name": "Hello",
  "version": "1.0.0",
  "arguments": [ { "name": "who", "direction": "In", "type": "String", "default": "World" } ],
  "root": { "id": "say", "type": "Core.Log", "properties": { "message": "'Hello, ' + who" } }
}
```

## Contributing

Read [CLAUDE.md](CLAUDE.md) for the architecture rules, phase discipline and conventions. Significant decisions get an
ADR in [docs/adr/](docs/adr/README.md).
