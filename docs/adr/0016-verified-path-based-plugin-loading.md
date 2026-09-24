# ADR-0016: Verified path-based plugin loading

- Status: Accepted
- Date: 2026-09-24
- Phase: 4
- Amends: ADR-0014 (assembly loading), ADR-0015 (integrity guarantees)

## Context
ADR-0014 loaded plugin assemblies **from memory**: each file was read, compared with the SHA-256 recorded when the
plugin directory was inspected, and then loaded with `LoadFromStream`. The Phase 3 review (issue B1) warned that this
leaves `Assembly.Location` empty.

The Phase 4 spike confirmed that this is a real failure. It loaded a minimal Playwright 1.63.0 plugin through the
real CLI plugin host:

```text
Microsoft.Playwright ALC=MyRPA.Plugin:Spike.Playwright Location=''
PlaywrightException: Driver not found: C:\Users\<user>\.dotnet\shared\.playwright\node\win32_x64\node.exe
```

Playwright finds its Node.js driver (`.playwright/`) relative to its own assembly. With no location, it derives a
wrong directory and cannot start. Many libraries that ship companion files, native helpers or configuration next to
their DLL behave the same way.

The spike also measured the second concern (B2, directory limits). A Playwright plugin built for one platform has
124 files and 104 MB (the 90 MB Node runtime is most of it). That is within the 2,048-file / 256 MB limits. An output
containing every platform's driver (`PlaywrightPlatform=all`) would be about 570 MB and would exceed them.

## Decision
- Plugin assemblies are loaded **from their path**, after verification. For every managed assembly and native
  library, `PluginLoadContext`:
  1. opens the file with `FileShare.Read` (readers allowed, writers and deleters denied);
  2. hashes the bytes read through that handle and compares them with the hash recorded during discovery;
  3. calls `LoadFromAssemblyPath` or `LoadUnmanagedDllFromPath` while the handle is still open;
  4. closes the handle.
- `Assembly.Location` is the real file inside the plugin directory, so location-relative libraries work.
- Everything else in ADR-0014 is unchanged:
  - one collectible context per plugin;
  - shared contract assemblies come from the host;
  - private dependencies are confined to the plugin directory through `.deps.json`;
  - a file that is not part of the verified content, or that changed after verification, is refused with
    `FileLoadException`;
  - the banned-API exemption still covers only `PluginLoadContext`.
- The directory limits (2,048 files, 256 MB) are **unchanged**. Plugins that bundle native runtimes are built for one
  platform (the default Playwright behaviour). A multi-platform bundle is not a supported package layout.
- The rejected alternative, `PLAYWRIGHT_DRIVER_SEARCH_PATH`, is a process-wide environment variable. Setting it from
  a plugin would mutate global state shared by every plugin and thread, and would fix only one library.

## Security implications
- **Windows.** The open handle blocks writers between verification and mapping, so the code that runs is the code
  that was verified. This is the same guarantee as in-memory loading.
- **Linux and macOS.** File sharing modes are advisory. Someone with write access to the plugin directory could
  replace a file between hashing and mapping, a window of milliseconds. Such an actor can already replace unpinned
  plugins at any time, so the change matters only for **pinned** plugins on non-Windows hosts. Operators who need
  that guarantee must keep plugin directories writable only by administrators. This is recorded as a known
  limitation.
- **Locking.** On Windows, loaded plugin files stay locked until the plugin context unloads. They cannot be updated
  in place while a host is running, which is desirable. Hosts must stop, or unload the plugin, before updating it.
- **Trust.** Nothing changes: in-process plugins are fully trusted, and `AssemblyLoadContext` is still not a security
  boundary (ADR-0015).

## Host requirement found by the spike
Playwright serializes its protocol with reflection-based `System.Text.Json`. A host built with
`JsonSerializerIsReflectionEnabledByDefault=false`, as trimmed or AOT hosts are, makes the Playwright plugin fail
with `InvalidOperationException: Reflection-based serialization has been disabled`. MyRPA hosts that load the
browser plugin must therefore stay non-AOT with reflection-based JSON enabled. That is the default for the CLI.

## Alternatives
- **Keep in-memory loading and set `PLAYWRIGHT_DRIVER_SEARCH_PATH`.** Rejected: it is global mutable state (see above).
- **Make the load mode a manifest option,** so that only some plugins use path loading. Rejected for now: it would
  mean two code paths and two security stories for little gain, because path loading with a verification lease is
  equivalent on Windows.
- **Raise the directory limits.** Not needed; the measured per-platform layout fits.

## Consequences
- The Playwright plugin loads and runs through the unchanged plugin lifecycle.
- The regression tests check that plugin assemblies have a real location inside the plugin directory, and that a
  file changed after verification is still refused.
- Temporary plugin copies used by tests may stay locked on Windows until their context unloads; test clean-up is
  best effort.
