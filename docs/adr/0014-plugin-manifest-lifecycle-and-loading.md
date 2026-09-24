# ADR-0014: Plugin manifest, lifecycle and AssemblyLoadContext loading

- Status: Accepted; the in-memory assembly loading below is superseded by [ADR-0016](0016-verified-path-based-plugin-loading.md) (verified path-based loading)
- Date: 2026-09-23
- Phase: 3
- Extends: ADR-0005 (explicit composition), ADR-0008 (banned APIs)

## Context
OpenRPA's `Plugins.LoadPlugins` worked like this (research D3, N3, N9):
- It ran `Assembly.Load` on every DLL in the application and `extensions` folders.
- It scanned every type in the AppDomain, instantiated plugin interfaces with `Activator.CreateInstance`, and never
  unloaded anything.
- Its `AssemblyResolve` handler also probed `%TEMP%`.

As a result, any DLL dropped in a folder executed, dependency versions collided globally, and the host referred to
plugins by hard-coded names. The PRD lifecycle is `Discover → Load → Initialize → Register → Execute → Dispose`.

## Decision

### Manifest (`myrpa-plugin.json`, manifest version 1.0)
Every plugin directory contains one manifest. Nothing about a plugin is learned by loading its code first.

| Field | Required | Meaning |
|---|---|---|
| `manifestVersion` | yes | `"1.0"`. Checked first; unsupported versions stop reading. |
| `id` | yes | Plugin id, `Namespace.Name` (case-insensitive uniqueness), e.g. `Contoso.Browser`. |
| `name`, `description` | name | Display text. |
| `version` | yes | SemVer subset `MAJOR.MINOR.PATCH[-prerelease]`. |
| `sdkVersion` | yes | SDK version built against (ADR-0013). |
| `targetFramework` | yes | `netX.Y`, `netX.Y-windows[ver]` or `netstandard2.0/2.1`. |
| `entryPoint` | yes | `{ "assembly": "<file>.dll", "type": "<full type name>" }`. The assembly is a file name only; the type is a non-generic, non-nested public class implementing `IPlugin`. |
| `capabilities` | no | Declared resource access: `FileSystem`, `Network`, `Process`, `Desktop`, `Clipboard`, `Credentials`, `NativeCode` (ADR-0015). |
| `activities` | no | Activity type names the plugin registers. Must not use `Core`. |
| `providers` | no | Provider ids the plugin registers. |
| `dependencies` | no | `[{ "id", "version" }]`: required plugins, same major version and at least `version`. |

`PluginManifestReader` reports every problem in one pass (codes MYRPA3002–3007). Unknown fields and capabilities are
warnings, so newer manifests stay readable. Deciding whether a plugin is compatible with *this* host is a separate
step.

### Lifecycle
`PluginLoader.LoadAsync(PluginHostOptions)` runs these steps:

1. **Discover.** Only the directories listed in the host's options are considered, and a directory is a plugin only
   if it contains `myrpa-plugin.json`. Nothing scans application, user or temp folders for DLLs.
2. **Validate.** Checks the manifest, SDK and framework compatibility, denied capabilities, the pinned digest, the
   entry assembly's presence, duplicate ids, activity or provider names declared twice, and the dependency graph:
   missing plugins, incompatible versions, cycles, and dependencies that were themselves rejected. **No plugin code
   runs in this step.**
3. **Load.** One collectible `PluginLoadContext` per plugin loads the entry assembly. The loader finds the manifest's
   type *inside that assembly only* and creates it through its public parameterless constructor. Dependencies load
   before their dependents.
4. **Initialize.** `IPlugin.Initialize(PluginContext)` receives the plugin's identity, directory, settings and the
   host SDK version.
5. **Register.** `IPlugin.Register(IPluginRegistrar)` stages registrations, and the whole set is validated before
   anything is accepted:
   - registered activities and providers must match the manifest exactly;
   - every registered type must be defined in the plugin's own load context, so a plugin can add services but never
     replace host services;
   - no service type may be registered twice.
6. **Use.** `PluginSet.AddTo(services)` applies the accepted registrations to the host's service collection:
   - activity registrations, marked with the plugin id;
   - providers, one singleton each, whose `Descriptor.Id` is checked;
   - plugin-lifetime and run-lifetime services;
   - instances;
   - `IPluginRegistry`.
7. **Dispose.** The host disposes its service provider first, which disposes providers and plugin services. Then
   `PluginSet.DisposeAsync()` disposes the plugin entry objects in reverse load order and unloads their contexts.

If a plugin fails any step, it gets diagnostics (MYRPA3001–3023), its entry object is disposed, its context is
unloaded, and **none** of its registrations are applied. The host is left exactly as if the plugin had not been
configured:
- a **required** source (the default) sets `HasRequiredFailures`, and the host must not start;
- an **optional** source is reported and skipped.

### AssemblyLoadContext design
- **One collectible context per plugin**, named `MyRPA.Plugin:<id>`.
- **Shared contract assemblies** always come from the default context, even if the plugin directory contains copies:
  - `MyRPA.Core`, `MyRPA.Workflow`, `MyRPA.Sdk`;
  - `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`.

  This keeps type identity (`IPlugin`, `IActivity`, `ILogger<T>`) intact across the boundary. Adding a shared assembly
  changes the plugin contract and requires an ADR.
- **Plugin-private dependencies** are resolved through `AssemblyDependencyResolver` from the plugin's `.deps.json`
  (plugins build with `EnableDynamicLoading`). They must be inside the plugin directory, and they load into the
  plugin's context. Two plugins can therefore use different versions of the same library, and a plugin can use a
  different version of a library the host also uses, as long as that library's types do not cross the boundary.
  Framework assemblies fall back to the default context.
- **Verified loading** *(amended by [ADR-0016](0016-verified-path-based-plugin-loading.md))*. Phase 3 loaded
  verified bytes from memory with `LoadFromStream`, which left `Assembly.Location` empty and broke Playwright. Every
  managed assembly and native library is now opened with read-only sharing, compared against the SHA-256 recorded when
  the directory was inspected, and loaded from its path while that handle is open.
- **Unloading.** Contexts are unloaded after disposal. Unloading completes only when nothing references plugin code
  any more. A plugin that leaves threads running, static event subscriptions or GC handles pointing into itself
  cannot be unloaded. The host does not depend on unload for correctness: in the CLI the process ends anyway. The
  tests prove the sample and fixture plugins do unload.
- **Banned APIs.** `AssemblyLoadContext.LoadFrom*` and `Assembly.GetType(string)` stay banned everywhere except in
  `MyRPA.Plugins.Loading.PluginLoadContext`. The exemption is data in the architecture rules, and the tests verify
  that it is used and does not leak to other types.

### Hosts
- The CLI accepts `--plugin <directory>` (repeatable). Plugins given on the command line are required. Load
  diagnostics go to stderr, and a failure exits with code **5**.
- `myrpa plugins` lists the loaded plugins, their activities, providers and digest.
- `myrpa info` marks activities that come from plugins.
- Configuration-file allow-lists (Robot, Orchestrator) come later and use the same `PluginHostOptions`.

## Alternatives
- **Scan a plugins folder for DLLs and reflect over types (OpenRPA).** Rejected: any dropped DLL would execute, and
  loading every assembly is slow.
- **One shared context for all plugins.** Rejected: plugins' dependency versions would collide, and nothing could be
  unloaded.
- **An assembly-level attribute naming the entry type,** instead of a type name in the manifest. Rejected for now: the
  manifest must be inspectable without loading code. The name lookup is confined to the entry assembly.
- **Out-of-process plugins (gRPC or named pipes).** This is the right isolation for hostile or crash-prone SDKs, such
  as the SAP, Java and COM bridges in OpenRPA. It is deferred: it is a transport for the same manifest and registrar
  contract, not a replacement for it (ADR-0015).
- **NuGet packages as the plugin format.** Deferred to package management (Phase 12). A package will unpack to the
  same directory layout.

## Consequences
- A plugin is a directory containing `myrpa-plugin.json`, the entry DLL, its `.deps.json` and its private
  dependencies. The sample plugin's build output is exactly that.
- Workflows cannot cause plugin loading: activity names in workflow files are resolved only against the catalog of
  already-loaded plugins.
- Plugin types never enter the default context, so tests reference plugin projects with
  `ReferenceOutputAssembly="false"`. The architecture tests enforce this.
