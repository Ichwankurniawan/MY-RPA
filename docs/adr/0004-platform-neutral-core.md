# ADR-0004: Platform-neutral Core with zero package dependencies

- Status: Accepted; amended by [ADR-0018](0018-studio-architecture.md) (the `MyRPA.Studio` shell alone may target `net10.0-windows` and use WPF)
- Date: 2026-09-23
- Phase: 1

## Context
PRD 7.1/10.1 require Core independence from WPF, Playwright, Windows UIA, browsers, AI, MCP, databases and
orchestrators. OpenRPA's contracts assembly referenced `FlaUI.UIA3`, `NLog`, WPF, `System.Activities.Presentation`
and `System.Runtime.Remoting` (`OpenRPA.Interfaces.csproj`, D5).

## Decision
- `MyRPA.Core` and `MyRPA.Workflow` target `net10.0` and have **no** `PackageReference` or `FrameworkReference`.
  They use only the BCL (`System.Diagnostics.ActivitySource` is part of the BCL on .NET 10).
- Core does not reference logging abstractions. Correlation values are exposed as plain key/value tags
  (`ExecutionIdentity.ToTags()`), which `MyRPA.Runtime` uses for both `ILogger.BeginScope` and `Activity` tags.
- Windows-only code will live in separate projects targeting `net10.0-windows` (Studio, Windows provider).
- Enforced by `MyRPA.Architecture.Tests` (project, package, assembly-reference and TFM rules).

## Alternatives
- Allow `Microsoft.Extensions.Logging.Abstractions` in Core — convenient for activities but not needed in Phase 1.
  Phase 2 may revisit when the activity execution contract is designed; that needs a new ADR.

## Consequences
- Core is usable from any host (CLI, Studio, Robot, server, tests) on any OS.
- CI builds and tests on Linux and Windows to prove neutrality.
