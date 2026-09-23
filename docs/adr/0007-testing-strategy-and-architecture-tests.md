# ADR-0007: Test stack and architecture-rule tests

- Status: Accepted
- Date: 2026-09-23
- Phase: 1

## Context
OpenRPA has no test projects (Phase 0). PRD 1.5 requires `dotnet test` to pass. The Phase 1 authorization requires
tests that verify architectural rules, not merely compilation.

## Decision
- Framework: **xUnit v3** (`xunit.v3` 4.x) running on **Microsoft.Testing.Platform (MTP)**. `global.json` opts
  `dotnet test` into MTP mode (`"test": { "runner": "Microsoft.Testing.Platform" }`), which the .NET 10 SDK requires
  for MTP-based frameworks. No VSTest packages (`Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`) are needed.
  Assertions use xUnit's `Assert` (no extra assertion library).
- Commands: `dotnet test --solution MyRPA.sln` (MTP syntax) or run a test project directly.
- One unit test project per `src` library, plus `MyRPA.Integration.Tests` (in-process host/CLI composition) and
  `MyRPA.Architecture.Tests`.
- Architecture tests are **dependency-free** (no NetArchTest/ArchUnitNET). They parse `.csproj` files, inspect
  compiled assembly references, and scan IL for banned calls using `System.Reflection` and
  `System.Reflection.Emit.OpCodes`.
- Test naming: `Subject_Condition_ExpectedResult`; tests are deterministic and need no network.
- Package versions are centrally managed in `Directory.Packages.props`.

## Alternatives
- NetArchTest.Rules / ArchUnitNET — capable, but add dependencies and do not read project files; our rules are simple.
- MSTest/NUnit — equally valid; xUnit is the common default for .NET OSS.

## Consequences
- Every detector has a known-bad self-test, and the rules were verified by injecting violations (a package and a
  `Type.GetType` call in Core; a `Runtime → Activities` reference). All were reported as failures.
- IDEs without MTP support fall back to running test projects as executables.
- Changing a rule means reviewing `ArchitectureRules.cs`.
- IL scanning covers compiled code, including compiler-generated async state machines.
