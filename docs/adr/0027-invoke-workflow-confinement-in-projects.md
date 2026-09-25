# ADR-0027: InvokeWorkflow confinement in server projects

- Status: **Decision accepted**: keep ADR-0012 confinement in W2. **Project-root resolver: Proposed**, not implemented.
  The owner reviewed it on 2026-09-25 and deferred it; entry-folder confinement stays unchanged until a later decision.
- Date: 2026-09-25
- Phase: Web Studio W2
- Relates to: [ADR-0012](0012-invoke-workflow-resolution-and-limits.md), [ADR-0022](0022-server-control-plane-and-project-structure.md), [ADR-0025](0025-local-mode-security.md)

## Context
`Core.InvokeWorkflow` resolves relative paths from the invoking workflow's directory. It is confined to the directory
of the entry workflow, the "workflow root" (ADR-0012, `FileWorkflowResolver`).

`MyRPA.Server` introduces projects: operator-registered folders, and the only files its API exposes (ADR-0025). In a
project, an entry workflow in `flows/` cannot invoke `lib/common.json`, because `lib/` is outside `flows/`.

What the code shows:
- The confinement root is whatever string the host passes as `WorkflowRunRequest.Location`, handed to
  `IWorkflowResolver.ResolveAsync(reference, invokingLocation, rootLocation)`.
- `FileWorkflowResolver` interprets `rootLocation` as the *entry workflow file* and confines to its directory.
- ADR-0012 already anticipates hosts plugging in other resolvers ("Studio project store") **without engine changes**.

## Decision (W2)
- **Behavior is unchanged in W2.**
  - The server passes the entry workflow's full file path as `Location` and uses `FileWorkflowResolver`. Invocations
    are confined to the entry workflow's directory, exactly as in the CLI.
  - Consequence for project layout: entry workflows that invoke others must sit at or above them. For example
    `project/main.json` can invoke `lib/x.json`; `project/flows/a.json` cannot.
- **The project root is a boundary of the server's file API only** (ADR-0025). It does not yet widen invocation.

## Proposal (not implemented; needs approval)
The smallest change that makes the project root the invocation boundary, if the owner wants it:

- **A server-side resolver, `ProjectWorkflowResolver`,** registered by `MyRPA.Server` in place of `FileWorkflowResolver`.
  - It resolves exactly like ADR-0012: relative paths only, `.json` only, 5 MB limit, full validation, per-run cache.
  - Its confinement root is **the registered project root** that contains the entry workflow, instead of the entry
    workflow's directory.
- **No change** to the engine, `IWorkflowResolver`, `WorkflowRunRequest`, `MyRPA.Storage` or the CLI.
  The project root is looked up from the entry workflow's path.
- **Security:** no widening beyond what the server's file API already exposes to the same user. Symlinked or
  reparse-point escapes are rejected like in the file API.
- **Tests:**
  - an invocation from `flows/` to `lib/` succeeds;
  - escaping the project root, absolute paths and non-JSON files fail;
  - the CLI behavior is unchanged.

## Alternatives
- **An optional confinement-root directory on `WorkflowRunRequest`:** an engine contract change and a change to
  `FileWorkflowResolver` semantics (file versus directory). Larger than needed; rejected.
- **Always confine to the project root in `FileWorkflowResolver`:** Storage would need to know about projects; rejected.
