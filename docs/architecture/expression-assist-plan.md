# Expression assist — completion, rename and usages (proposed)

Status: **approved** by the owner on 2026-10-09 ("continue E-1"); E-1 and E-2 done, E-3 next. Decision record:
[ADR-0041](../adr/0041-expression-assist.md). Not a PRD phase: an authorized follow-up slice, like the debugger
(ADR-0040). Phase 7 still needs its own authorization.

## Goal
Writing expressions should not depend on remembering names:
- **Completion:** while typing in an expression field, the Studio offers the names in scope at that activity (arguments,
  variables, loop items and other locals) and the expression functions, with a short hint for each.
- **Rename:** renaming a variable, argument or local changes every expression and assignment that uses it, exactly. No
  text search, so `item` inside a string or `x.item` is never touched.
- **Find usages:** for any variable or argument, see which activities use it and jump to them.

## What exists to build on
- **The expression parser** (`MyRPA.Workflow.Expressions`): every name node already carries its position, and
  `WorkflowExpression` lists `ReferencedNames` and `ReferencedFunctions`.
- **The function whitelist** (`ExpressionFunctions`): name and arity for each function, with no description yet.
- **The loader's scope rules** (`WorkflowSemanticValidator`): which names are visible at each node, including locals
  from `scopeSlots` (ForEach `itemVariable`, TryCatch `exceptionVariable`). This is the rule that reports `MYRPA1044`
  unknown names.
- **The Studio:** live validation, the expression property editors (inline on cards and in Properties), the
  Variables and Arguments tabs, and Select-node navigation.

## Rules
- **The server owns the knowledge.** Names in scope, functions and references come from the server, computed by the
  same code as validation. The browser never re-implements scope or parsing rules (Studio conventions, ADR-0028).
- **No editor library.** The completion list is the Studio's own small component (ADR-0032 decided against CodeMirror;
  that stands). The CSP stays as it is.
- **Undo:** a rename is one undoable edit. It is refused, with the reason, if the new name is invalid or already used in
  that scope, or if the document changed while the rename was being prepared.
- **Performance:** typing stays within the keystroke target (p95 ≤ 50 ms on the 3,000-node fixture). The list filters
  in the browser; the server is asked once per field and document version, never per keystroke.
- **Accessibility:** the field is a combobox with a listbox (ARIA combobox pattern); everything works by keyboard and
  passes the a11y scan.

## Design

### Server (E-1)
- `GET /api/expressions/functions` → `[{ name, minArguments, maxArguments, signature, description }]`. Each whitelist
  entry gains a signature and a one-line description; a test fails if one is missing.
- `POST /api/expressions/scope { document, path }` → `[{ name, kind: Argument | Variable | Local, type }]`: the names
  visible at the node at `path` (the JSON path diagnostics already use). It comes from the validator's own scope walk,
  exposed through `WorkflowLoader`, and is best effort for documents with errors, like validation.
- `POST /api/expressions/references { document, path, name }` → the declaration that `name` resolves to at `path`, and
  every reference to that same declaration: `[{ path, start, length }]`. References cover expressions (from the
  parser's name positions; member names and string contents never match) and name properties (assignment targets,
  `LocalName`, name maps).
  - **Scope-aware:** two loops that both declare `item` are two different declarations. Renaming one never touches
    the other, and a name hidden by an inner local is not renamed.
- Same security as `POST /api/validate` (session, Origin, anti-forgery); no workflow is stored.

### Studio
- **Completion (E-2):**
  - **Opening it:** typing a name prefix in any expression field opens a list of matching names and functions;
    Ctrl+Space opens it on demand.
  - **Keys:** Up and Down move, Enter or Tab accepts, Esc closes.
  - **Hints:** a function inserts `name(` and shows its signature while the cursor is inside the call.
  - **Kinds:** each entry shows its kind (argument, variable, local, function) and its type or signature.
  - **Server calls:** scope is fetched when the field gets focus, cached per document version and node; functions are
    fetched once per connection.
- **Rename and usages (E-3):**
  - **Where:** a **Rename…** action in the Variables and Arguments tabs (and for a loop's item variable on its card).
  - **The dialog** shows how many uses will change. Applying it renames the declaration and every reference in one
    undoable edit, from the positions the server returned.
  - **Usages (n)** on each variable and argument row lists the activities that use it; choosing one selects and reveals
    its card.

## Slices

| Slice | What | Done when |
|---|---|---|
| E-1 Server | Function signatures and descriptions; the scope and references APIs, from the loader's scope walk and the parser's positions | Workflow tests: scope at sequence, slot, loop body and catch nodes; references with locals hiding outer names, member access, strings, name maps. Server tests: the three endpoints, invalid documents, security |
| E-2 Completion | The combobox popup on every expression field (inline and Properties), filtering, keyboard, function signature hint | Vitest; smoke: complete a variable and a function by keyboard, the server finds no problems; a11y with the list open; perf keystroke p95 unchanged |
| E-3 Rename and usages | Rename… with use count (one undo step, refusals), Usages (n) with select and reveal | Vitest: shadowed locals untouched, strings and members untouched, undo, a stale document refused; smoke: rename a used variable, the workflow still validates and runs |

## Out of scope
- Syntax highlighting.
- Type checking or type inference of expressions.
- Completion of dictionary keys or list members.
- Renames across files (arguments of invoked workflows).
- Rename of activity ids.

## E-1 result (2026-10-09)

- **Workflow:**
  - `WorkflowExpression.NameReferences`: every name, with its start and length in the text;
  - `ExpressionFunctions.Functions`: name, arity, signature and description;
  - `WorkflowLoader.IndexNames(json)` returns a `WorkflowNameIndex` with `Symbols`, `InScope(path)`,
    `Resolve(path, name)` and `ReferencesTo(symbol)`.
- **How it works:** the index is filled by the semantic validator's own walk through an optional recorder. Its scopes
  are the validator's scopes, each declaration is one `WorkflowSymbol`, and normal loading records nothing and reports
  the same diagnostics.
- **Server:** `GET /api/expressions/functions`, `POST /api/expressions/scope` and `POST /api/expressions/references`
  (see `server.md`), with the same session, Origin and anti-forgery checks as validation.
- **Tests:**
  - Workflow `WorkflowNameIndexTests` (7): scopes at the root, a loop body, the loop's own properties and a catch
    slot; exact references (declaration, assignment target, expression; never members or text in strings);
    same-named locals in two loops kept separate; documents with errors; unchanged diagnostics; name positions;
    every function described.
  - Server `ExpressionAssistTests` (7): the three endpoints, bad input, session and anti-forgery.

## E-2 result (2026-10-09)

- **Studio:**
  - **`ExpressionInput`:** the Studio's own combobox (ARIA combobox and listbox, no library), used for expression
    properties (inline and in Properties), expression map values and transition conditions.
  - **Names:** from `POST /api/expressions/scope`, asked when a field gets focus and cached per document version and
    path (`Studio.namesInScope`).
  - **Functions:** fetched once at connect (`expressionFunctions` in the store).
  - **Filtering:** names come before functions, ignoring case. The list never opens inside a string, after `.` or in a
    number.
  - **Hint:** the signature of the call the caret is in.
- **Tests:**
  - Vitest `ExpressionUi.test.tsx` (8);
  - smoke: completing `lower(toString(n))` by keyboard against the real server (the variable listed before `now()`),
    and it validates;
  - a11y: the list open, 0 violations;
  - perf: keystroke p95 31.8 ms, all targets met.
- **Also hardened:** the smoke drag helper now centres the dragged card and checks that both pointer points are really
  on their targets. The one-off W7-2 failure on Windows CI (#18) would now fail with a clear message instead of a wrong
  drop.
