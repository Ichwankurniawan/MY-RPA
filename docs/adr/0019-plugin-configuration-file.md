# ADR-0019: Plugin configuration file

- Status: Accepted
- Date: 2026-09-25
- Phase: 5
- Builds on: ADR-0014 (plugin loading), ADR-0015 (trust model), ADR-0016 (integrity pins)

## Context
`PluginHostOptions` supports several host-level controls:
- per-plugin SHA-256 pins;
- `Required`;
- per-plugin settings;
- `RequireIntegrity`;
- denied capabilities.

Before this ADR, the CLI could only pass plugin directories (`--plugin`), so none of these controls were usable from
the command line. Studio needs the same allow-list, and both hosts should read it the same way.

## Decision
- `MyRPA.Plugins.PluginConfigurationFile` reads a JSON file (comments and trailing commas allowed):

  ```json
  {
    "pluginConfigVersion": "1.0",
    "requireIntegrity": true,
    "deniedCapabilities": [ "FileSystem" ],
    "plugins": [
      { "directory": "plugins/browser", "sha256": "<64 hex>", "required": true, "settings": { "key": "value" } }
    ]
  }
  ```

- Validation is strict:
  - Unknown properties are errors, so a misspelt security option is never silently ignored.
  - `pluginConfigVersion` must be `1.0`.
  - Digests must be 64 hexadecimal characters.
  - Settings are strings.
  - A directory may appear only once.
  - Errors raise `PluginConfigurationException` with the JSON path.
- Relative directories are resolved against the file's own directory, never the working directory.
- `CreateHostOptionsAsync(directories, configPath)` merges the file with directories named on the command line:
  - Those directories are required and unpinned.
  - If a directory is also in the file, the file's entry wins.
- The CLI gains `--plugin-config <file>` (given once). An invalid file exits with code 5, the same as a plugin failure.
- Studio accepts the same options: `MyRPA.Studio [workflow.json] [--plugin <dir>]... [--plugin-config <file>]`.
- There is no implicit default file location. Plugins are loaded only when the operator names them (ADR-0015).

## Consequences
- Operators can pin plugins and deny capabilities for CLI runs and in Studio.
- The format is versioned independently of the workflow and manifest formats.
