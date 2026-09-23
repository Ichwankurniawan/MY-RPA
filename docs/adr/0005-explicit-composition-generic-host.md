# ADR-0005: Explicit composition with Generic Host and DI; no global state

- Status: Accepted
- Date: 2026-09-23
- Phase: 1

## Context
OpenRPA relies on singletons and statics (`RobotInstance.instance`, `global.webSocketClient`, `Plugins.*`,
`Config.local`, `WorkflowInstance.Instances`), side effects in singleton getters, `async void` handlers, and
reflection-based discovery of everything loaded in the AppDomain (D3, D6).

## Decision
- Each library exposes an `IServiceCollection` extension (`AddMyRpaRuntime`, `AddMyRpaActivities`, `AddMyRpaStorage`).
- Composition roots (Phase 1: `MyRPA.Cli`) build a `Microsoft.Extensions.Hosting` host. Only composition roots
  reference `Microsoft.Extensions.Hosting`.
- The service provider is built with `ValidateOnBuild` and `ValidateScopes`.
- Activity types are registered explicitly (`AddActivityDescriptor(...)`); there is no assembly scanning.
- Forbidden in `src`: mutable static fields, static service locators, `async void`, `Type.GetType(string)`,
  `Assembly.Load*`. Enforced by `MyRPA.Architecture.Tests`.
- `TimeProvider` is injected (registered as `TimeProvider.System`) so later timeouts are testable.

## Alternatives
- A third-party container (Autofac, etc.) — not needed; built-in DI covers the foreseeable needs.
- `System.CommandLine` for the CLI — deferred to Phase 2 when real commands (`run`, `validate`) with options arrive.
  Phase 1 uses a small explicit command dispatcher registered through DI.

## Consequences
- Every dependency is visible in constructors and testable in isolation.
- `const` names and immutable `static readonly` values remain allowed.
