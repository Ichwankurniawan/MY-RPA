# ADR-0020: Activity catalog snapshot

- Status: Accepted
- Date: 2026-09-25
- Phase: 5
- Builds on: ADR-0010 (activity metadata), ADR-0011 (validation pipeline)

## Context
Studio builds its toolbox, designer and property editors from `ActivityDescriptor` metadata. Other tools need the same
metadata without loading the engine or plugin assemblies:
- a future web Studio;
- documentation;
- AI assistance (a later phase).

## Decision
- `MyRPA.Workflow.Serialization.ActivityCatalogJson` writes and reads a catalog snapshot:

  ```json
  { "catalogVersion": "1.0", "activities": [
      { "type": "Core.Log", "displayName": "Log", "category": "Diagnostics", "description": "…",
        "allowsChildren": false,
        "properties": [ { "name": "message", "kind": "Expression", "required": true,
                          "allowedValues": [], "scopeSlots": [] } ],
        "slots": [ { "name": "case:", "required": false, "prefix": true } ] } ] }
  ```

- The format is strict. Invalid snapshots raise `FormatException`.
- `ActivityCatalogSnapshot` implements `IActivityCatalog`, so the `WorkflowLoader` can validate against a snapshot.
  Tests show that the diagnostics are identical to those produced with the live catalog.
- `myrpa catalog` prints the snapshot, including plugin activities loaded with `--plugin` / `--plugin-config`.

## Consequences
- Metadata can be published and consumed across process and platform boundaries without executing plugin code.
- A snapshot describes what a host could run. Running still requires the real activities.
