# ADR-0015: Plugin trust model

- Status: Accepted; integrity at load time is amended by [ADR-0016](0016-verified-path-based-plugin-loading.md) (verified path loading: equivalent on Windows, a small window on Linux/macOS)
- Date: 2026-09-23
- Phase: 3
- Extends: ADR-0008 (secure-by-default baseline)

## Context
Plugins are .NET code that runs inside the MyRPA process. It is tempting to treat per-plugin `AssemblyLoadContext`s
as a security boundary. **They are not.** An `AssemblyLoadContext` controls which assemblies are loaded and how
their names are resolved. It provides no permission checks: plugin code can use reflection, P/Invoke, file and
network I/O, and process start just like host code.

.NET has no in-process sandbox (Code Access Security does not exist on .NET Core). OpenRPA loaded any DLL it found
(research N3).

## Decision
**Every in-process plugin is fully trusted code.** The system never claims otherwise, and the trust decision is
made by the host operator before loading, through these mechanisms:

| Mechanism | Phase 3 status | What it guarantees | What it does not |
|---|---|---|---|
| Explicit allow-list (`PluginHostOptions.Sources`, CLI `--plugin`) | Implemented | Only directories the operator named are considered; nothing is discovered | That the named plugin is benign |
| Manifest validation | Implemented | Structure, identity, declarations and dependencies are consistent before code runs | Anything about behaviour |
| SDK / framework / version compatibility | Implemented | The plugin was built for a contract this host implements | Correctness |
| Dependency validation | Implemented | Required plugins are present, compatible and acyclic | — |
| Registration validation | Implemented | Registrations match the manifest; plugins only add their own types, never replace host services | That plugin code cannot tamper with the process by other means |
| Integrity pinning (`PluginSource.Sha256`, `RequireIntegrity`) | Implemented | The directory content equals what the operator reviewed; every assembly is re-verified at load | Who produced the content |
| Declared capabilities + `DeniedCapabilities` | Implemented (declarative) | Policy on what plugins *declare* they need, and a basis for review | Enforcement: a plugin can do undeclared things |
| Signature validation (Authenticode or NuGet author signing) | **Not implemented** | — | Deferred to package management (Phase 12) |
| Process isolation | **Not implemented** | — | Needed for untrusted or unstable plugins; the Robot and Orchestrator phases decide |

Rules:
- Load only plugins you would be willing to run as your own code. Documentation, CLI help and diagnostics say so.
- Plugin settings (`PluginContext.Settings`) are not secret storage. Credentials come from the credential
  infrastructure (Phase 11), never from manifests or settings.
- Workflow data never selects code:
  - activity names resolve only against already-loaded registrations;
  - manifests are read only from allow-listed directories;
  - the entry type is looked up only inside the plugin's own entry assembly;
  - `Type.GetType(string)`, `Assembly.Load*`, `AssemblyLoadContext.LoadFrom*` and `Assembly.GetType(string)` are
    banned outside `PluginLoadContext`.
- Links (symlinks, junctions) anywhere in a plugin directory reject the plugin. The confinement and the digest cover
  real files only.
- The digest covers the whole directory: SHA-256 over the sorted `path\nsha256\n` lines of all files. `myrpa plugins`
  prints it so an operator can pin exactly what was reviewed.

## Alternatives
- **"Sandbox" plugins in separate `AssemblyLoadContext`s.** Rejected as a security claim: an ALC is not a security
  boundary.
- **Require signing now.** Rejected: there is no package pipeline or trust store yet. Pinning gives integrity today
  without an unresolved identity question.
- **Enforce capabilities at run time,** for example by blocking `HttpClient` in plugin contexts. Rejected: it cannot
  be enforced in-process and would give a false sense of safety. Only process isolation with OS-level controls can
  enforce it.

## Consequences
- Operators decide trust explicitly, can pin content, and can deny capabilities by policy. The system states plainly
  that loaded plugins run with full host permissions.
- Untrusted plugin execution needs out-of-process hosting. That is recorded as a future requirement for the Robot and
  Orchestrator phases.
- Declared capabilities give Studio, Orchestrator and AI tooling reviewable metadata without executing plugin code.
