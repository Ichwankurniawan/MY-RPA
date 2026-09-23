# ADR-0006: Observability foundation — ILogger scopes and ActivitySource from day one

- Status: Accepted
- Date: 2026-09-23
- Phase: 1 (foundation); Phase 11 adds exporters and the metrics platform

## Context
OpenRPA used a static string logger (`OpenRPA.Interfaces/Log.cs`) and thread-local instance correlation
(`OpenRPA/Tracing.cs`) — unstructured and broken by async (D13). Its OpenTelemetry spans per workflow/activity with
propagated `traceId/spanId` were valuable (R12, R17).

## Decision
- Logging: `Microsoft.Extensions.Logging` (`ILogger<T>`), structured message templates, scopes.
- Tracing: `System.Diagnostics.ActivitySource` named `MyRPA.Runtime` (constant in
  `MyRPA.Core.Diagnostics.DiagnosticNames`), owned by the DI singleton `MyRpaTelemetry` (disposed with the container,
  not static).
- Correlation keys are fixed constants used for **both** log scopes and span tags:
  `myrpa.execution.id`, `myrpa.workflow.id`, `myrpa.node.id`, `myrpa.correlation.id`.
- `IExecutionScopeFactory.Begin(ExecutionIdentity, operationName)` opens a logger scope and starts an `Activity`
  together.
- CLI logs go to **stderr** so stdout stays machine-readable.
- No OpenTelemetry SDK/exporter package in Phase 1; any OTel-compatible listener can subscribe to `MyRPA.*` sources.

## Alternatives
- Serilog/NLog — extra dependency; M.E.Logging is the PRD choice and provider-agnostic.
- The OpenTelemetry SDK now — no consumer yet; violates "smallest dependency set".

## Consequences
- The Phase 2 engine uses the same scope factory per execution and per node.
- Log/span key names are a public contract; changing them needs an ADR.
