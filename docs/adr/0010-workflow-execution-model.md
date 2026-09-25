# ADR-0010: Workflow execution model, contracts and lifetimes

- Status: Accepted; activity lifetime (transient from the run scope) superseded by [ADR-0013](0013-automation-sdk-and-activity-contract.md) (one instance per invocation, disposed by the engine)
- Date: 2026-09-23
- Phase: 2
- Amends: ADR-0003 (Activities may reference Workflow; Activities may use Logging.Abstractions)

## Context
PRD 2.1 lists `Workflow, Activity, ActivityContext, ExecutionContext, WorkflowRunner, ActivityResult, WorkflowExecution`.
Phase 1 review items: M1 (IDs read the system clock), M3 (scopes cannot record failure; no per-execution DI scope).
OpenRPA's WF4 host blocked the runtime thread from the designer and swallowed errors (D6, D7, N8).

## Decision

### Names (PRD concept → MyRPA type)
| PRD concept | MyRPA | Project |
|---|---|---|
| Workflow | `WorkflowDefinition` (+ `ArgumentDefinition`, `VariableDefinition`, `NodeDefinition`) | Workflow |
| Activity | `IActivity` (behavior), `ActivityDescriptor` (metadata), `NodeDefinition` (usage in a workflow) | Workflow / Core / Workflow |
| ActivityContext | `IActivityContext` | Workflow |
| ExecutionContext | `ExecutionIdentity` (ids) + internal `ExecutionFrame`/`VariableScope` (state). Not named `ExecutionContext` to avoid `System.Threading.ExecutionContext` | Core / Runtime |
| WorkflowRunner | `IWorkflowRunner` / `WorkflowRunner` | Workflow / Runtime |
| ActivityResult | `ActivityResult` | Workflow |
| WorkflowExecution | `WorkflowExecutionResult` (+ `ExecutionError`) | Workflow |

### Contract placement
Execution contracts (`IActivity`, `IActivityContext`, `IActivityFactory`, `IWorkflowRunner`, `IWorkflowResolver`,
results, exceptions) live in `MyRPA.Workflow` because they need the workflow model; Workflow stays BCL-only and
platform-neutral. Future plugins (Phase 3) implement `IActivity` by referencing Core + Workflow only.
`MyRPA.Activities` now references Workflow (and `Microsoft.Extensions.Logging.Abstractions` for `Core.Log`).
`MyRPA.Runtime` still does **not** reference `MyRPA.Activities`; it resolves activities through `IActivityFactory`.

### Resolution chain
`ActivityTypeName` → `IActivityFactory` (implemented by `ActivityCatalog`) → implementation type registered with
`AddActivity<T>(descriptor)` → instance resolved from the **execution's DI scope** → `ExecuteAsync(IActivityContext)`.
No assembly scanning; the type comes from code registration, never from workflow text.

### Execution semantics
- Failures are exceptions. At each node boundary a non-workflow exception is wrapped once in
  `WorkflowActivityException(nodeId, activityType, inner)`, so the result points to the node that failed.
- `TryCatch` catches `WorkflowActivityException` only. Cancellation (`OperationCanceledException` for the run's token)
  is never caught by `TryCatch`; `finally` still runs.
- Terminal statuses (`ExecutionStatus`, Core): `Succeeded`, `Failed`, `Cancelled` (caller token), `TimedOut` (run or
  invocation timeout). The runner returns a `WorkflowExecutionResult`; it does not throw for workflow failures.
- Invalid run arguments (unknown, missing required, wrong type) produce a `Failed` result without executing.
- Timeouts use `CancellationTokenSource(TimeSpan, TimeProvider)`, so tests control time with a fake clock.
- `ActivityResult` is currently `Completed` only; it is the extension point for future outcomes (e.g. suspension for
  human approval, PRD 9.5).

### Variables and arguments
- Workflow-level `arguments` (In/Out/InOut) and `variables`, plus locals introduced by `ForEach.itemVariable` and
  `TryCatch.exceptionVariable` (visible only in their slot). No shadowing.
- `In` arguments and locals are read-only; variables and Out/InOut arguments are assignable.
- Values are restricted to the canonical value model (`WorkflowValues`): `null`, `string`, `long` (Int), `decimal`,
  `bool`, `DateTimeOffset`, read-only list, read-only string-keyed dictionary. Every variable is nullable.
  Only Int→Decimal widening is implicit.

### Lifetimes (M3)
| Service | Lifetime |
|---|---|
| `IWorkflowRunner`, `IActivityFactory`/`IActivityCatalog`, `WorkflowLoader`, `IIdGenerator`, `TimeProvider`, `MyRpaTelemetry`, `IExecutionScopeFactory` | Singleton (stateless or thread-safe) |
| `IWorkflowResolver` (with per-run cache) | Scoped — one DI scope per top-level run |
| Activities | Transient, resolved from the run's scope |
Nested `InvokeWorkflow` executions share the parent's DI scope (and resolver cache) but get their own
`ExecutionId`, linked through `ParentExecutionId`.

### Identifiers (M1)
`IIdGenerator` (Core) creates `ExecutionId`/`CorrelationId`. The default `TimeOrderedIdGenerator` (Runtime) produces
UUIDv7 from the injected `TimeProvider`. The static `ExecutionId.New()`/`CorrelationId.New()` are removed so that
nothing bypasses the injected generator.

### Observability (M3)
`IExecutionScope.Complete(ExecutionStatus, Exception?)` sets the span status (Ok/Error), records the exception
(`Activity.AddException`) and the `myrpa.outcome` tag. Spans: `workflow.execute` per execution and one per node named
after the activity type (`Core.If`), tagged with `myrpa.activity.type`. New tag keys: `myrpa.activity.type`,
`myrpa.parent_execution.id`, `myrpa.outcome`. Workflow `Core.Log` messages use the log category `MyRPA.Workflow.Log`.

## Alternatives
- Result-object error propagation instead of exceptions — verbose for every activity and easy to ignore.
- Contracts in Core — Core would need the workflow model; it would stop being a thin identity/metadata layer.
- Singleton activities — fine today, but scoped/transient keeps room for per-run state (browser sessions in Phase 4).

## Consequences
- ADR-0003's allow-list is amended as above (enforced by `MyRPA.Architecture.Tests`).
- A buggy activity that throws is always reported as a node failure; there is no silent failure path.
