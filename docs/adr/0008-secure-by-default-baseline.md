# ADR-0008: Secure-by-default baseline

- Status: Accepted; extended by [ADR-0012](0012-invoke-workflow-resolution-and-limits.md) (process and network APIs banned; file confinement)
- Date: 2026-09-23
- Phase: 1 (baseline); Phase 11 extends

## Context
Phase 0 found in OpenRPA: .NET Remoting IPC with `TypeFilterLevel.Full` open to all local users (N1), an extension
that `eval`s code from a local process (N2), loading every DLL including from `%TEMP%` (N3), remote execution on by
default and a default public cloud URL (N4), a plain-text password setting (N5), `Type.GetType` on remote input (N6),
and robots with broad DB rights (N13).

## Decision
Phase 1 code — and later phases, unless a superseding ADR says otherwise — must not introduce:
1. .NET Remoting, `BinaryFormatter`, or type-name-driven deserialization of untrusted input.
2. `Type.GetType(string…)`, `Assembly.Load*`, `Activator.CreateInstance(string…)` in `src` (checked by IL scan).
   Phase 3 plugin loading will get its own ADR and a narrowly scoped, audited exception.
3. Network listeners or remote execution enabled by default. Phase 1 opens no ports and makes no network calls.
4. Default external endpoints in configuration.
5. Secrets in workflow files or plain-text settings. Workflows will reference credentials by name (credential
   references) resolved at runtime by a credential provider (Phase 11).
6. Dynamic code execution (`eval`-style scripting) without an explicit, sandboxed design ADR.

## Alternatives
- Defer security to Phase 11 — rejected; PRD §18 requires security to be designed in.

## Consequences
- Convenience features such as "load any DLL from a folder" need explicit design and review.
