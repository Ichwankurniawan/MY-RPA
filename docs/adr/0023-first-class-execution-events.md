# ADR-0023: First-class execution events (engine observer hook)

- Status: **Proposed**. Awaiting owner approval. Not implemented; implementation would be part of W1.
- Date: 2026-09-25
- Phase: Web Studio W0
- Amends: ADR-0010 (execution contracts). ADR-0006 (observability) is unchanged.
- Related: ADR-0022 (Execution.Hosting), ADR-0024 (streaming)

## Context
Studio currently learns about node progress by listening to the engine's tracing spans (`ActivitySource("MyRPA.Runtime")`) and filtering by correlation id. That was acceptable for one run in one desktop process. For a multi-user server, Agents and Robots it is the wrong foundation. From the code and the W0 spike:

1. **Node outcomes exist only on spans.** `IExecutionScope.Complete` returns early when no span was created, and a span exists only if a listener samples it. Product behavior, such as showing a node as failed, would depend on observability sampling.
2. **Observability configuration can silently remove product events.** An OpenTelemetry exporter with a sampling policy, or no listener at all, changes what Studio sees.
3. **Listeners are process-global.** Each one sees every run of every user, and routing to the right client is done by string-comparing tags. That is a cross-tenant leakage risk once there are several users.
4. **Performance is not the deciding factor.** The W0 bench ran 20 concurrent runs of 1,000 nodes:
   - one shared listener: about 0.5 µs per node;
   - one listener per run (the Phase 5 pattern): about 1.1 µs per node;
   - the prototype's shared listener was always active, so there is no true zero-listener baseline.

   The reasons for change are correctness and isolation.

## Decision (proposed)
- **Contract** in `MyRPA.Workflow.Execution` (base libraries only):

  ```csharp
  public interface IExecutionObserver
  {
      void OnEvent(ExecutionEvent executionEvent);
  }

  public abstract record ExecutionEvent(ExecutionId ExecutionId, ExecutionId? ParentExecutionId, CorrelationId CorrelationId, DateTimeOffset Time);
  public sealed record ExecutionStarted(...,  WorkflowId WorkflowId) : ExecutionEvent(...);
  public sealed record NodeStarted(...,       NodeId NodeId, ActivityTypeName ActivityType) : ExecutionEvent(...);
  public sealed record NodeCompleted(...,     NodeId NodeId, ActivityTypeName ActivityType, ExecutionStatus Status, ExecutionError? Error) : ExecutionEvent(...);
  public sealed record ExecutionCompleted(...,ExecutionStatus Status, ExecutionError? Error, TimeSpan Duration) : ExecutionEvent(...);
  ```

  One method with a closed set of event records, so new event kinds can be added without breaking implementers.
- **Registration is per run, never global.** It is a new optional `WorkflowRunRequest.Observer`. The engine carries it through `ExecutionFrame` to child invocations; child events carry their own execution id and the parent's. Nothing goes in DI or static state (ADR-0005).
- **Emission points** are the same places the engine already opens and completes execution scopes: workflow start and finish (including argument rejection), and node start and complete (succeeded, failed, cancelled). Events are emitted **whether or not any span is sampled**.
- **Delivery rules:**
  - Synchronous, on the executing flow, in execution order for a given execution.
  - Observers must be fast and non-blocking. Hosts enqueue into a bounded channel; Execution.Hosting does that.
  - **An observer exception must never change the run's outcome.** The engine catches it, logs it, and stops calling that observer for the run. This is a deliberate, documented exception to "never swallow exceptions": a display consumer must not break automation.
  - Cancellation of the run token is not observed through the hook.
- **Content:** ids, types, statuses, and errors (code, message, node). **No variable values or outputs** until secret handling exists (Phase 11). The final outputs remain in `WorkflowExecutionResult`.
- **Logs are not part of the hook.** They stay on `ILogger`. Hosts route them by the execution and correlation ids in the engine's logging scopes, which the W0 spike showed works per execution.
- Spans and logging scopes stay unchanged for observability (ADR-0006).
- **SDK impact: none.** Activities don't see the observer, and SDK 1.0 is not changed.

## Alternatives considered
- **Keep span listeners, with one shared dispatching listener.** Rejected: context points 1–3.
- **A global `IExecutionObserver` in DI.** Rejected: process-global, needs filtering, and has the same leakage risk.
- **`RunAsync` returning an `IAsyncEnumerable` of events.** Rejected: it forces a buffering and backpressure policy into the engine. Hosts can build that on top of the observer.

## Tests required when implemented
- Event order for sequences, slots and loops.
- Child invocations: ids and parent ids.
- Failure, cancellation, timeout and argument rejection.
- Events with no span listener attached.
- An observer that throws does not change the run's outcome.
- Concurrent runs deliver no cross-run events.
- Overhead measured against the Phase 5 baseline.
