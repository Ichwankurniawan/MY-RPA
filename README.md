# MyRPA

A modern, extensible RPA platform in C#/.NET 10, inspired by OpenRPA and redesigned from the
[Phase 0 research](docs/research/openrpa-analysis.md). Product requirements: [MyRPA-PRD.md](MyRPA-PRD.md).

**Status:** Phase 1 — Foundation. The solution structure, dependency boundaries, core contracts, DI composition,
logging/tracing foundation, CLI shell, tests and CI exist. There is **no workflow execution yet** (Phase 2).

## Repository layout

```text
MyRPA.sln
src/
  MyRPA.Core          platform-neutral contracts (no dependencies)
  MyRPA.Workflow      the single workflow model
  MyRPA.Activities    activity catalog (+ built-in activities from Phase 2)
  MyRPA.Runtime       runtime composition, execution scopes, logging/tracing
  MyRPA.Storage       storage boundary (no database)
  MyRPA.Cli           `myrpa` command-line host (composition root)
tests/
  MyRPA.*.Tests       unit tests per library
  MyRPA.Integration.Tests   real CLI host, in-process
  MyRPA.Architecture.Tests  dependency and code rules
docs/
  architecture/       overview and Phase 1 reconciliation
  adr/                architecture decision records
  research/           Phase 0 OpenRPA research
reference/            read-only OpenRPA clone for research (git-ignored)
```

Architecture: [docs/architecture/overview.md](docs/architecture/overview.md).

## Development setup

Prerequisites:
- **.NET SDK 10.0.100 or later** (`global.json` rolls forward to the latest 10.0.x feature band).
  Check with `dotnet --list-sdks`.
- Git. Any IDE with .NET 10 support (Visual Studio 2026, Rider, VS Code + C# Dev Kit).

No other tools, databases, browsers or services are needed in Phase 1.

## Build

```bash
dotnet build MyRPA.sln
```

Warnings are errors, nullable reference types are on, and .NET analyzers run in `Recommended` mode
(`Directory.Build.props`).

## Test

```bash
dotnet test --solution MyRPA.sln
```

Tests use xUnit v3 on Microsoft.Testing.Platform; `global.json` opts `dotnet test` into that mode.
`MyRPA.Architecture.Tests` fails if a boundary is crossed (for example Core referencing a package, or Runtime
referencing Activities).

## Run the CLI

```bash
dotnet run --project src/MyRPA.Cli -- info
```

```text
Usage: myrpa [--verbose] <command> [arguments]
Commands:  info | help | version
```

Results go to stdout; logs go to stderr (`--verbose` enables debug logs).

## Contributing

Read [CLAUDE.md](CLAUDE.md) for the architecture rules, phase discipline and conventions. Significant decisions get an
ADR in [docs/adr/](docs/adr/README.md).
