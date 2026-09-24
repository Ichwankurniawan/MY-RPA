# ADR-0011: Workflow JSON format v1.0 and the validation pipeline

- Status: Accepted
- Date: 2026-09-23
- Phase: 2

## Context
PRD 2.6 requires a versioned JSON format; PRD 10.6 requires migrations once published. Phase 1 review item M2: model
constructors throw on the first problem, so an invalid file could not be reported completely.

## Decision

### Format
Documented in `docs/architecture/workflow-format.md`. Top level: `schemaVersion` ("1.0"), `id`, `name`, `version`,
optional `description`, `arguments[]`, `variables[]`, `root` (node). Node: `id`, `type`, optional `displayName`,
`properties{}`, `children[]`, `slots{}` (named single children, e.g. `then`, `else`, `body`, `try`, `catch`, `case:gold`).

### Pipeline (M2)
```text
JSON text → parse (System.Text.Json JsonDocument)     → MYRPA1001 malformed JSON (stop)
          → schema version check                        → root not object / missing / malformed / unsupported (stop)
          → structural read (RawWorkflow + paths)      → shape/type/required/unknown-field diagnostics
          → semantic validation (catalog, names, refs)  → all remaining diagnostics in one pass
          → WorkflowDefinition (only when zero errors)
```
The version is checked before the structure because it decides how the rest of the document is interpreted
(a future migration step slots in right after it).
- Diagnostics are structured: `Code` (`MYRPAxxxx`), `Severity`, `Message`, JSON `Path`, optional `NodeId`.
- Domain constructors keep *invariants only* (non-null, identifier formats, unique node ids); user-facing validation
  lives in the pipeline and never throws for bad input.
- Unknown fields are **warnings** (forward compatibility); unknown properties of an activity are **errors**
  (typos would otherwise silently change behavior).

### Versioning
- Reader supports schema major 1, minor ≤ `WorkflowSchemaVersion.Current.Minor`. A newer minor or another major is
  rejected with `MYRPA1011` ("requires a newer MyRPA").
- Minor bumps are additive; major bumps require a migration step inserted between parse and structural read.

### Serializer
`System.Text.Json` (`JsonDocument` + `Utf8JsonWriter`), no reflection-based `JsonSerializer` for workflow files, no
polymorphic type-name handling. No third-party JSON library.

## Alternatives
- `JsonSerializer` into DTOs — stops at the first error and hides paths; polymorphic nodes need type discriminators.
- JSON Schema validation library — extra dependency; semantic checks (catalog, scoping) are needed anyway.

## Consequences
- Tools (Studio, AI generation, CI) can call the same `WorkflowLoader` and get every problem at once.
- `WorkflowJsonWriter` produces the same format, so load → save round-trips.
