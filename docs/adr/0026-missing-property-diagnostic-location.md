# ADR-0026: Missing-property diagnostics point at the property

- Status: Accepted, implemented in W2
- Date: 2026-09-25
- Phase: Web Studio W2
- Amends: [ADR-0011](0011-workflow-json-format-and-validation-pipeline.md) (validation pipeline diagnostics)

## Context
`MYRPA1040` ("Required property 'X' of 'Type' is missing") was located at the node's whole property collection
(`$.root.children[0].properties`). Every other property diagnostic uses the path of the property itself
(`$.root.children[0].properties.<name>`, from `WorkflowStructureReader`). So editors could not show this error on the
missing property's editor. ADR-0021 exit criterion 4 requires exactly that.

## Decision
- `MYRPA1040` is reported at `<node path>.properties.<name>`: the path where the missing property would be. This is the
  same path form as present properties.
- Nothing else changes:
  - the code, severity, message text and node id stay the same;
  - `ValidationDiagnostic` gets no new field;
  - no other diagnostic changes.

## Consequences
- **Backward compatible except for the more precise path.**
  - Tools that compare paths by prefix still match the node.
  - No shipped sample or CLI behavior depends on the old path.
- **Clients** can map the diagnostic to the property by parsing the trailing `.properties.<name>`. Studio.Core's mapper
  already does this, so the frozen WPF Studio shows the error on the property editor without code changes. One WPF
  test assertion that documented the old, node-level location was updated.
- **Regression test:** `WorkflowLoaderTests.MissingProperty_IsLocatedAtThePropertyItself`.
