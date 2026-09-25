# ADR-0022: MyRPA.Server control plane and project structure

- Status: Accepted. W1 built `MyRPA.Contracts` and `MyRPA.Execution.Hosting`. W2 built `MyRPA.Server` in local mode; see [server.md](../architecture/server.md). W3 added serving the built
  `web/studio` (`--web`) and the first Web Studio slice (ADR-0028); its wire types are hand-written for now (ADR-0028
  decision 6).
- Date: 2026-09-25
- Phase: Web Studio W0
- Amends: ADR-0003 (new projects and dependency direction), ADR-0004 and ADR-0008 (ASP.NET Core allowed in server executables only)
- Related: ADR-0021, ADR-0023, ADR-0024, ADR-0025

## Context
The Web Studio needs a backend for:
- activity catalogs;
- authoritative validation;
- projects and files;
- execution;
- event streaming.

Later, Agents, Robots, a remote CLI mode and an orchestrator attach to the same control plane.

The W0 spike checked the proposed shape against the repository:
- An ASP.NET Core minimal API built under the repository's `Directory.Build.props` (analyzers, warnings as errors). It hosted the real engine and served the catalog snapshot (ADR-0020) unchanged. It validated 217–236 KB documents with `WorkflowLoader` in about 5–8 ms at p50 on the server.
- It routed workflow logs per execution through the engine's logging scope keys.
- .NET 10's built-in `TypedResults.ServerSentEvents` needed no extra package.

## Decision

### Projects
| Project | Kind | References | Responsibility |
|---|---|---|---|
| `MyRPA.Contracts` | library, base libraries only | none | API DTOs and, later, agent/robot messages; `System.Text.Json` source generation. W1: execution event messages. |
| `MyRPA.Execution.Hosting` | library | Core, Workflow, Contracts (engine contracts only; the host composes Runtime) | Run coordination, per-execution event capture via ADR-0023, log routing by execution/correlation id, bounded replay buffers, cancellation, concurrency limits. Shared by the Server now and by Agent/Robot later. |
| `MyRPA.Server` | executable, composition root, ASP.NET Core | Contracts, Execution.Hosting, Runtime, Activities, Storage, Plugins | Projects and files, per-target catalogs, validation, executions, SSE (ADR-0024), security (ADR-0025). Serves the built `web/studio` assets. |
| `web/studio` | npm/Vite, outside the solution | — | The only Studio UI (ADR-0021). Types are generated from the server's OpenAPI document. |
| later: `MyRPA.Agent`, `MyRPA.Robot` | executables | Contracts, Execution.Hosting, … | Execution plane. Connects outbound to the server. |

- **There is no `MyRPA.Workflow.Authoring` project.** Editing, undo and clipboard live in the client. If AI/MCP later needs server-side edits, an edit-operation contract is designed then (Phase 8/9).
- **Project management lives in the Server only.** Agents receive documents from the server and don't need projects.

### Dependency rules (enforced in architecture tests when the projects are added)
- Engine and library projects (Core, Workflow, Activities, Runtime, Storage, Sdk, Plugins) never reference Contracts, Execution.Hosting or Server.
- `Microsoft.AspNetCore` is allowed only in the Server executable (later also Agent and Robot).
  - The rule has to check compiled references. `Microsoft.NET.Sdk.Web` adds the ASP.NET Core framework reference implicitly, so it never appears in the project file text.
- Nothing references a composition root.
- `Contracts` depends only on the base libraries.

### Hosting constraints found in W0
- **No trimming or native AOT for the Server, Agent or Robot.** Plugins rely on reflection-based `System.Text.Json` (ADR-0016), so hosts must not disable it globally. API DTOs may add source-generated serialization.
- **Plugin attribution** (activity to plugin) comes from `LoadedPlugin.Manifest.Activities`, which is already public. No plugin-API change is needed.
- **Sub-workflow confinement.** `InvokeWorkflow` confinement is rooted at the *entry workflow's folder* (ADR-0012), not at a project root. With project-based storage, a workflow in `project/flows/` can't invoke `project/lib/x.json`.
  - Options for W2, needing a decision there: keep the ADR-0012 semantics and document the project layout, or add an optional confinement root to `WorkflowRunRequest` (an additive engine contract change, with its own ADR).

## Consequences
- W1 builds Contracts and Execution.Hosting. W2 builds the Server in local mode.
- The architecture tests gain rules for the new projects.
- The architecture test project will need a framework reference to ASP.NET Core to inspect the Server assembly. It is a test project, so that's allowed.
