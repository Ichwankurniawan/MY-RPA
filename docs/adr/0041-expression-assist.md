# ADR-0041: Expression assist — completion, rename and usages from the server

- Status: **Accepted** (owner, 2026-10-09: "continue E-1"). Not part of a PRD phase; a follow-up slice like the
  debugger (ADR-0040). Plan: [expression-assist-plan.md](../architecture/expression-assist-plan.md).
- Builds on: ADR-0009 (expressions), ADR-0028 (the Studio never duplicates loader rules), ADR-0032 (no CodeMirror).

## Context
Expressions are typed from memory. A typo only shows up as `MYRPA1044` (unknown name) from live validation, and
renaming a variable or argument leaves every expression that uses it broken. Both are listed as deferred in
`web-studio.md`.

## Decision
1. **The server answers, the Studio asks.** Three read-only endpoints serve this, computed by the same code that
   validates workflows (the semantic validator's scopes and the expression parser):
   - the expression functions, with signatures and descriptions;
   - the names in scope at a node;
   - the references to a name's declaration, with exact positions.
   The Studio holds no copy of these rules.
2. **Scope-aware references.** A reference belongs to the declaration it resolves to. Same-named locals in two loops
   are separate, and an inner local hides an outer name. Positions come from the parser, so member names and text
   inside strings are never references.
3. **Our own completion component.** It is a combobox with a listbox on the existing expression inputs, with no editor
   library. ADR-0032 stands.
4. **A rename is one edit.** The declaration and every reference change together, as one undo step. The rename is
   refused if the name is invalid or clashes, or if the document changed after the references were read.

## Alternatives considered
- **Computing scopes and references in the browser:** rejected, because it duplicates loader rules (ADR-0028), and
  the two would drift.
- **Text search and replace:** rejected, because it renames inside strings and member names and ignores scopes.
- **CodeMirror 6:** rejected again (ADR-0032), because the inputs are one line and it would be the first editor
  dependency.

## Consequences
- **`MyRPA.Workflow` gains public read APIs** (names in scope at a path; name references with positions) and
  function metadata. They are additive; the SDK and the workflow format are unchanged.
- **Three new server endpoints,** with the same security as `POST /api/validate`.
- **Not in this decision:** syntax highlighting, type checking, completion of dictionary keys, renames across files.
