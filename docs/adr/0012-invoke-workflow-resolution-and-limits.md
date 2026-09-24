# ADR-0012: InvokeWorkflow resolution, confinement and limits

- Status: Accepted
- Date: 2026-09-23
- Phase: 2
- Extends: ADR-0008

## Context
`InvokeWorkflow` runs another workflow. OpenRPA resolved workflows by id/relative filename from a global store and
allowed remote invocation (`InvokeRemoteOpenRPA`). A file reference in a workflow is user input: it must not read
arbitrary files, recurse forever, or leak state between runs.

## Decision
- Resolution goes through `IWorkflowResolver` (Workflow). Phase 2 provides `FileWorkflowResolver` (Storage).
- References are **relative paths** resolved against the directory of the invoking workflow. Absolute paths, paths that
  escape the directory of the entry workflow (the "workflow root"), and non-`.json` files are rejected.
- Files larger than 5 MB are rejected. Invoked workflows go through the full validation pipeline (ADR-0011).
- The resolver is **scoped per top-level run** and caches definitions by full path, so a loop that invokes the same
  workflow parses it once, and nothing is cached globally.
- Nesting depth is limited by `WorkflowRuntimeOptions.MaxInvocationDepth` (default 10). Recursion is allowed within
  that limit; exceeding it fails the invoking node (catchable by `TryCatch`).
- Child executions: new `ExecutionId`, same `CorrelationId`, `ParentExecutionId` set, parent token linked, optional
  per-invocation timeout. Arguments are mapped explicitly (`arguments`: child In/InOut ← parent expression;
  `outputs`: parent variable ← child Out/InOut). A failed, timed-out or cancelled child fails the invoking node;
  cancellation of the parent propagates as cancellation.

Phase 2 also extends the ADR-0008 banned-API list (IL scan in `MyRPA.Architecture.Tests`): `Process.Start`,
`HttpClient`, `WebClient`, `WebRequest.Create`, `Socket`, `TcpClient`, `TcpListener`, `UdpClient`. Activities that need
network or processes (PRD Phase 7) will require a superseding ADR.

## Alternatives
- Resolve by workflow id from a registry — needs a store/catalog of workflows (future package system, Phase 12).
- Unlimited recursion — a typo becomes a stack overflow or an endless run.

## Consequences
- Workflows that invoke others must live under the entry workflow's directory.
- Hosts can plug in other resolvers (Studio project store, orchestrator package store) without engine changes.
