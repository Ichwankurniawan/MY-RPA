# ADR-0013: Automation SDK and the frozen activity contract

- Status: Accepted
- Date: 2026-09-23
- Phase: 3
- Amends: ADR-0003 (adds `MyRPA.Sdk` and `MyRPA.Plugins`), ADR-0010 (activity lifetime)

## Context
Phase 3 opens MyRPA to plugins. Once third-party code compiles against the activity contract, changing it breaks
plugins, so the contract must be frozen *before* plugin loading exists. The Phase 2 review found three problems in it:

1. Activities were registered **transient** and resolved from the run's DI scope. The container tracks disposable
   transients until the scope ends, so an `IDisposable` activity inside a loop of a million iterations was retained
   for the whole run: effectively a memory leak, and the wrong owner for resources such as browser sessions.
2. `ActivityResult` meant only "completed", and it was undecided whether it should carry status, errors or values.
3. The cancellation behaviour was implemented, but it was not documented as a contract.

OpenRPA fused runtime behaviour, designer UI and configuration in WF4 activity classes. It had no cancellation, used
blocking sleeps, and put every capability in the monolithic `IRecordPlugin` (research D2, D4, D5).

## Decision

### Layers and projects
```text
Workflow engine (MyRPA.Runtime)          executes definitions; knows only IActivity/IActivityFactory
      ↓ contracts (MyRPA.Core, MyRPA.Workflow)
Automation SDK (MyRPA.Sdk)               plugin contract + provider/element/selector abstractions
      ↓ implemented by
Plugin / provider (separate assemblies)  activities + technology interface + provider implementation
      ↓ uses
Technology (Playwright, UIA, HTTP, ...)  only ever referenced by provider assemblies
```
- **`MyRPA.Sdk`** is new, references only Core and Workflow, and is BCL-only. The SDK surface a plugin compiles
  against is `MyRPA.Sdk` + `MyRPA.Workflow` (`IActivity`, `IActivityContext`, `ActivityResult`,
  `ActivityFailedException`, values) + `MyRPA.Core` (`ActivityDescriptor`, `ActivityTypeName`, identity). The activity
  contracts stay in `MyRPA.Workflow`: the engine consumes them and must not depend on the SDK.
- **`MyRPA.Plugins`** is new: the plugin host (ADR-0014). It references Core, Workflow, Sdk and Activities, and only
  composition roots reference it.
- The engine, the built-in activities and storage never reference `MyRPA.Sdk` or `MyRPA.Plugins`. This is enforced by
  the architecture tests.
- `AutomationSdk.Version` is **1.0**. Plugins declare the SDK version they need. A host accepts a plugin with the same
  major version and a minor version no higher than its own. Minor versions only add members; major versions may
  break plugins.

### Activity identity
- Activity types are identified by `ActivityTypeName`: `Namespace.Name` (two or more segments of ASCII letters and
  digits), for example `Browser.Click`. The name is stable, serialized in workflows, used by validation and listed in
  plugin manifests. It never contains a version and is never a CLR type name.
- The `Core` namespace is reserved for built-ins. A plugin owns the namespaces of the names its manifest declares. Two
  plugins declaring the same name is a load-time conflict.
- A behaviour change that breaks existing workflows needs a **new name** (or a new plugin major version); the meaning
  of an existing name does not change silently.

### Activity lifetime (resolves the Phase 2 concern)
- **One instance per node invocation.** `IActivityFactory.Create` builds the instance with a precompiled
  `ActivatorUtilities` factory from its single public constructor, whose parameters come from the run's services. The
  instance is **not** a DI service, so no container tracks it.
- The **engine owns it**: after `ExecuteAsync` it disposes the instance (`IAsyncDisposable` preferred, otherwise
  `IDisposable`), whether the invocation succeeded, failed or was cancelled.
  - If disposal throws after a success, the node fails.
  - If disposal throws after a failure or cancellation, the error is logged and the original outcome is kept.
- Resources that outlive one invocation have an explicit owner (`PluginServiceLifetime`):

| Owner | Lifetime | Disposed | Use for |
|---|---|---|---|
| Activity instance | one node invocation | after the invocation | nothing expensive |
| `Run` service (scoped) | one top-level run, shared with invoked workflows | when the run ends, in any status | browser sessions, native handles, per-run caches |
| `Plugin` service or provider (singleton) | host lifetime; thread-safe | at host shutdown | clients, pools, drivers |

- There is no transient lifetime for plugin services.
- Constructor dependencies of every registered activity are checked against the container when the catalog is
  built, so a missing service fails at startup rather than at the first node.

### ActivityResult is frozen
- `ActivityResult` has one outcome in SDK 1.0: `Completed`. It carries no status, error, values or metadata, because
  each of those already has exactly one channel:
  - **Failure** is an exception. Throw `ActivityFailedException(errorType, message)` for a classified failure; its
    `ErrorType` (for example `ElementNotFound`) becomes the node's `errorType` with code `MYRPA2001`, which
    `Core.TryCatch` exposes. Any other exception is also a node failure, classified by its CLR type name.
  - **Cancellation** is an `OperationCanceledException` of the run token.
  - **Output values** are assigned with `IActivityContext.SetValue` to names declared by the node's
    assignment-target properties. Other names are rejected at runtime, so data flow stays visible without running
    the workflow.
  - **Diagnostics** go to logs and traces.
- `ActivityResult` describes one node. `WorkflowExecutionResult` describes a whole run: its status (Succeeded, Failed,
  Cancelled or TimedOut), outputs, timing and the attributed error. The engine derives the run result from exceptions
  and outputs, and activities never construct one.
- A future outcome, such as suspension for human approval (PRD 9.5), would be added as a new static member in a minor
  SDK version. Existing activities keep working because `Completed` keeps its meaning.
- The SDK adds `AutomationException : ActivityFailedException` and `AutomationErrorTypes`:
  - `ElementNotFound`, `AmbiguousMatch`, `ElementStale`, `InvalidSelector`, `NotSupported`, `ProviderUnavailable`.
  - `OperationTimeout`: an operation-level wait expired. This is a catchable failure, distinct from the run's
    `TimedOut` status.

### Cancellation contract
- `IActivityContext.CancellationToken` is cancelled when the run is cancelled or any timeout that applies to it
  elapses: its own, or that of a workflow that invoked it. Activities pass it to every awaited operation and let
  `OperationCanceledException` propagate. They must not catch and swallow it.
- Cancellation is **cooperative**. The engine never aborts or abandons a running activity: it awaits the activity,
  then starts no further nodes. Synchronous code that ignores the token cannot be interrupted. A provider whose
  technology cannot cancel an operation must stop waiting on it when the token fires, release or poison the affected
  session, and return promptly. A host that must bound hung work isolates it in a process (Robot, Phase 10).
- Cleanup after cancellation is allowed: `finally` blocks and `DisposeAsync` run. Cleanup must be bounded and uses
  its own token or timeout, never the cancelled one.
- An `OperationCanceledException` that the run token did not cause (for example, an activity's own internal timeout)
  is a node **failure**, not a cancellation.
- `IActivityContext.Deadline` exposes the earliest applicable timeout for technologies that take a timeout value
  instead of a token. The token remains authoritative.

### Execution context is minimal
`IActivityContext` exposes the following, and nothing else:
- the node;
- identity (execution, correlation, workflow and node ids);
- the cancellation token and deadline;
- the `TimeProvider`;
- typed property access;
- `SetValue` for declared targets;
- execution of the node's own children and slots;
- `InvokeWorkflowAsync`.

There is no `GetService`, no access to the service provider, and no access to engine internals. Services come
through the activity's constructor. Logs written through an injected `ILogger<T>` are correlated with the execution
and node automatically, because the engine opens a logging scope and a span per node.

### Automation abstractions (contracts only in Phase 3)
- `IAutomationProvider` (with `AutomationProviderId` and `AutomationProviderDescriptor`) marks the boundary to a
  technology.
  - Each technology defines its own interface deriving from it (PRD 7.2: `IBrowserProvider → PlaywrightProvider`),
    and activities depend on that interface.
  - Providers are registered by id and resolved by constructor injection.
- `IAutomationElement` is a technology-neutral handle with the common operations: click, type text, get text, get
  attribute.
  - Technologies extend it for operations that are not universal.
  - Elements are runtime objects owned by their provider or session and are never stored in workflow variables.
- Selectors (`Selector`, `SelectorStep`, `SelectorStrategies`, `ISelectorResolver`, `SelectorMatch`) are data: a
  provider id and an ordered path of `strategy=value` steps. Resolution is technology-specific. The workflow-file
  syntax and ranking of alternatives belong to Phase 6.
- **Not created:** `IRecorderPlugin` and `ITrigger`. Nothing in Phase 3 consumes them, and designing them without a
  recorder (Phase 6) or triggers (Phase 10) would be speculative. The documented starting points are:
  - Recorder: user event → recorder → recorded interaction → activity.
  - Trigger: an event source that starts or resumes runs.

## Alternatives
- **Keep transient-from-scope and require stateless activities.** Rejected: the retention is caused by the container
  and happens even for well-behaved disposable activities, and nothing enforced "stateless".
- **Create and dispose an explicit DI scope per node.** Rejected: it gives the same ownership at a higher cost, and
  run-lifetime services would then need a second, outer scope anyway.
- **Rich `ActivityResult` (status, error, outputs).** Rejected: it would duplicate exceptions, `SetValue` and
  `WorkflowExecutionResult`, and it would create two ways to fail a node.
- **Move `IActivity` into `MyRPA.Sdk`.** Rejected: the engine would then depend on the SDK, and the plugin contract
  would be tied to the engine assembly's release cycle.
- **An `IServiceProvider` on the context.** Rejected: that is a service locator (ADR-0005).

## Consequences
- The Phase 2 retention problem is gone. The tests prove disposal per invocation, and prove that earlier instances are
  collectable while the run is still going.
- Activities no longer appear in the DI container. `ActivityCatalog` validates their constructors at startup instead.
- `SetValue` rejects names that are not declared targets. All built-ins already complied.
- The SDK is small: one plugin interface, one registrar, a provider marker, an element, a selector model and one
  failure type.
- Before a public SDK release, the SDK assemblies need an assembly-version policy tied to the SDK version. Today they
  are versioned with the product, 0.1.0 (technical debt).
