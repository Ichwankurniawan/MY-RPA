# Workflow Format v1.1

Status: current. Version 1.0 (Phase 2) describes tree workflows; version 1.1 adds graph workflows (flowcharts): node
`transitions`, node `layout` and graph containers (§3.1). Decisions:
[ADR-0011](../adr/0011-workflow-json-format-and-validation-pipeline.md) (format and validation),
[ADR-0009](../adr/0009-constrained-expression-language.md) (expressions),
[ADR-0012](../adr/0012-invoke-workflow-resolution-and-limits.md) (InvokeWorkflow),
[ADR-0037](../adr/0037-flowchart-and-state-machine-workflows.md) (graph workflows, format 1.1).
Examples: [`samples/`](../../samples).

## 1. Document

```json
{
  "schemaVersion": "1.0",
  "id": "hello-world",
  "name": "Hello World",
  "version": "1.0.0",
  "description": "optional",
  "arguments": [
    { "name": "userName", "direction": "In", "type": "String", "default": "World" },
    { "name": "greeting", "direction": "Out", "type": "String" }
  ],
  "variables": [
    { "name": "count", "type": "Int", "default": 0 }
  ],
  "root": { "id": "main", "type": "Core.Sequence", "children": [ ... ] }
}
```

| Field | Required | Meaning |
|---|---|---|
| `schemaVersion` | yes | File-format version `"major.minor"`. This build reads `1.0` and `1.1`. Checked first; anything else stops validation. A file that uses `transitions`, `layout` or a graph container declares `1.1` (MYRPA1058 otherwise); other files stay `1.0`. |
| `id` | yes | Workflow id: 1–128 chars from `[A-Za-z0-9-_.:]`. |
| `name` | yes | Display name (not blank). |
| `version` | yes | The author's content version (free text, e.g. SemVer). Unrelated to `schemaVersion`. |
| `description` | no | Free text. |
| `arguments` | no | Array of argument definitions. |
| `variables` | no | Array of variable definitions. |
| `root` | yes | The root node. |

JSON comments and trailing commas are accepted. Unknown fields produce **warnings** (forward compatibility).

## 2. Arguments and variables

| Field | Arguments | Variables | Meaning |
|---|---|---|---|
| `name` | yes | yes | Letters, digits, `_`; not starting with a digit; ≤ 64 chars; not `true`/`false`/`null`. Names are unique across arguments and variables. |
| `direction` | yes | — | `In` (read-only inside the workflow), `Out` (returned), `InOut` (supplied, assignable, returned). |
| `type` | yes | yes | `String`, `Int` (64-bit), `Decimal`, `Boolean`, `DateTime` (ISO 8601; no offset = UTC), `Object` (any), `List`, `Dictionary`. |
| `required` | no | — | In/InOut only: the caller must supply a value. |
| `default` | no | no | JSON value of the declared type. Not allowed on `Out`. |

Every type admits `null`. The only implicit conversion is Int → Decimal.

## 3. Nodes

```json
{
  "id": "check-total",
  "type": "Core.If",
  "displayName": "optional label",
  "properties": { "condition": "total > 100" },
  "children": [ ],
  "slots": { "then": { ... }, "else": { ... } }
}
```

- `id` — unique within the workflow (children and slots included), same character rules as workflow ids.
- `type` — a registered **activity type name** `Namespace.Name` (e.g. `Core.If`). Never a .NET type name.
- `properties` — values interpreted by the activity's property **kind**:

| Kind | JSON | Meaning |
|---|---|---|
| Expression | string, number, boolean, null | A string is **expression text** (quote string literals inside it: `"'Hello ' + name"`); a number/boolean/null is a literal. |
| Text | string | Plain text, never evaluated; may be restricted to allowed values. |
| AssignmentTarget | string | Name of a variable or Out/InOut argument. |
| LocalName | string | Declares a read-only local visible only in specific slots (e.g. ForEach `itemVariable` in `body`). Must not hide an existing name. |
| ExpressionMap | object | Name → expression. |
| AssignmentTargetMap | object | Name → assignment target. |

- `children` — ordered list; only for activities that allow it (`Core.Sequence`, and the graph containers of §3.1).
- `slots` — named single children; which names are allowed is defined by the activity (prefix slots such as `case:<value>`).
- `transitions` (1.1) — only on a step of a graph container; see §3.1.
- `layout` (1.1) — `{ "x": number, "y": number }`, the node's position on a designer canvas. Finite numbers; the
  engine ignores it.

### 3.1 Graph containers (1.1)

An activity whose descriptor declares `childLayout: Graph` (`Core.Flowchart`) runs its `children` as the **steps** of
a graph. The first child is the **start step**. Each step may list `transitions`; after the step completes, the engine
takes the first transition whose `when` is true or absent and runs that sibling next. When no transition is taken, the
container completes.

```json
{ "id": "retry", "type": "Core.Flowchart", "properties": { "maxSteps": 100 },
  "children": [
    { "id": "attempt", "type": "Core.Assign", "properties": { "to": "attempts", "value": "attempts + 1" },
      "layout": { "x": 240, "y": 160 }, "transitions": [ { "to": "check" } ] },
    { "id": "check", "type": "Core.Decision",
      "transitions": [ { "to": "done", "when": "attempts >= 3", "label": "yes" }, { "to": "attempt", "label": "no" } ] },
    { "id": "done", "type": "Core.Log", "properties": { "message": "'finished'" } }
  ] }
```

| Transition field | Required | Meaning |
|---|---|---|
| `to` | yes | Id of a sibling step in the same graph container (MYRPA1053). |
| `when` | no | Expression (like an Expression property) that must be Boolean; absent means always. Evaluated in the container's scope after the step. |
| `label` | no | Text shown on the arrow; designer only. |

A `Core.StateMachine` is a graph container whose steps are `Core.State` nodes (MYRPA1055 otherwise). A state runs its
`entry` slot, then (unless it is final) its transitions choose the next state, then its `exit` slot runs; a state that
is not final and takes no transition fails with MYRPA2011. A final state (`final` true) ends the machine and has no
transitions (MYRPA1057 when `final` is the literal `true`; at run time, MYRPA2011). Example:
[`samples/state-machine.json`](../../samples/state-machine.json) (init → get work ⇄ process → end).

Steps share the container's scope (no locals). Every run of a step emits its own `node.started` / `node.completed`
events, so a step in a loop appears once per run. Example: [`samples/flowchart.json`](../../samples/flowchart.json).

## 4. Built-in activities

| Type | Properties | Children / slots | Behavior |
|---|---|---|---|
| `Core.Sequence` | — | `children` | Runs children in order; stops at the first failure. |
| `Core.Assign` | `to` (target, req), `value` (expr, req) | — | Stores the value, converted to the target's declared type. |
| `Core.Log` | `message` (expr, req), `level` (text: Trace, Debug, Information, Warning, Error, Critical) | — | Writes to log category `MyRPA.Workflow.Log`. |
| `Core.Delay` | `milliseconds` (expr → Int ≥ 0, req) | — | Asynchronous wait on the injected clock; cancellable. |
| `Core.If` | `condition` (expr → Boolean, req) | `then` (req), `else` | |
| `Core.Switch` | `expression` (expr, req) | `case:<text>`, `default` | Runs the case whose name equals the value formatted as text (exact, case-sensitive). |
| `Core.While` | `condition` (req), `maxIterations` (expr → Int) | `body` (req) | Checks before each iteration; exceeding `maxIterations` fails the node. |
| `Core.DoWhile` | `condition` (req), `maxIterations` | `body` (req) | Runs the body at least once. |
| `Core.ForEach` | `items` (expr → List, req), `itemVariable` (local, req), `indexVariable` (local) | `body` (req) | Locals visible only in `body`. |
| `Core.TryCatch` | `exceptionVariable` (local, visible in `catch`) | `try` (req), `catch`, `finally` | The error is a Dictionary: `message`, `code`, `nodeId`, `activityType`, `errorType`. Cancellation/timeouts are never caught; `finally` is skipped once cancelled. |
| `Core.Throw` | `message` (expr, req) | — | Fails with code `MYRPA2002`. |
| `Core.InvokeWorkflow` | `workflow` (text, req), `arguments` (map), `outputs` (target map), `timeoutMilliseconds` (expr) | — | Runs another workflow file (relative path, confined to the entry workflow's directory). |
| `Core.Flowchart` (1.1) | `maxSteps` (expr → Int ≥ 0; default 10,000) | `children` as graph steps (§3.1) | Runs the start step, then follows transitions; checks cancellation between steps. Running more than `maxSteps` steps fails the node with `MYRPA2010`. |
| `Core.Decision` (1.1) | — | — | Does nothing; a flowchart branch point whose transitions carry the conditions. |
| `Core.StateMachine` (1.1) | `maxSteps` (expr → Int ≥ 0; default 10,000) | `children`: `Core.State` steps (§3.1) | Runs the first state, then the state each one chooses, until a final state completes; more than `maxSteps` states fails with `MYRPA2010`. |
| `Core.State` (1.1) | `final` (expr → Boolean; default false) | `entry`, `exit` | `entry`, then the transitions choose the next state (conditions only), then `exit`. Not final and no transition taken: `MYRPA2011`. |

`myrpa info` prints the registered activity types.

Plugins add activity types in their own namespaces (for example `Demo.Echo`, `Demo.GetField` from the sample plugin);
`Core.*` is reserved for the built-ins. A workflow that uses a plugin activity is valid only in a host that loaded that
plugin (`myrpa --plugin <dir> validate ...`); otherwise it reports MYRPA1033. See
[plugin-system.md](plugin-system.md).

## 5. Expressions

| Feature | Syntax |
|---|---|
| Literals | `42`, `3.14`, `'text'` or `"text"` (escapes `\\ \' \" \n \t \r`), `true`, `false`, `null`, `[1, 2]`, `{'key': value}` |
| Names | variables, arguments, locals |
| Operators (low → high) | `\|\|` · `&&` · `== !=` · `< <= > >=` · `+ -` · `* / %` · unary `! -` · `x[i]`, `x.key` |
| Functions | `len upper lower trim contains startsWith endsWith substring replace toString toInt toDecimal toBoolean toDateTime now append keys hasKey isNull coalesce abs min max round` |

Rules: Int ÷ Int truncates; Int with Decimal gives Decimal; arithmetic overflow and division by zero are errors;
`+` concatenates when either side is a String (`null` → empty); `&& || !` need Booleans and short-circuit;
`==` compares numbers by value and lists/dictionaries deeply; `.key` works only on dictionaries; `now()` uses the
injected clock. There is no access to .NET members or types. Limits: 4096 characters, nesting depth 64.

## 6. Validation diagnostics

`myrpa validate` prints `error|warning CODE PATH: message` for every problem at once.

| Code | Problem |
|---|---|
| MYRPA1001 | Not valid JSON |
| MYRPA1002 | Root is not an object |
| MYRPA1003 / 1004 / 1005 | Missing field / wrong JSON type / unknown field (warning) |
| MYRPA1010 / 1011 | Malformed / unsupported schema version |
| MYRPA1020 / 1021 | Invalid workflow id / blank name or version |
| MYRPA1030 / 1031 | Invalid / duplicate node id |
| MYRPA1032 / 1033 | Malformed / unregistered activity type |
| MYRPA1040 / 1041 / 1042 | Missing / unknown property / wrong value shape |
| MYRPA1043 / 1044 | Expression syntax, function or arity error / unknown name |
| MYRPA1045 / 1046 / 1047 | Invalid assignment target (unknown or read-only) / value not allowed / local hides a name |
| MYRPA1050 / 1051 / 1052 | Children not allowed / unknown slot / missing required slot |
| MYRPA1053 / 1054 / 1055 | Transition target is not a sibling step / transitions on a node whose parent is not a graph container / graph container without steps, or a state machine step that is not a `Core.State` |
| MYRPA1056 | Step cannot be reached from the start step (warning) |
| MYRPA1057 | Layout position is not a finite number, or a final state (`final` literally true) with transitions |
| MYRPA1058 | `transitions`, `layout` or a graph container in a file that declares a schema version before 1.1 |
| MYRPA1060 / 1061 / 1062 / 1063 / 1064 / 1065 | Invalid name / duplicate name / invalid direction / invalid type / invalid default / Out argument with default or required |
| MYRPA1066 | A secret property (catalog 1.2, ADR-0042) has a literal value: it must read an argument or variable, so no password or token is written in the file |

## 7. Execution error codes

| Code | Meaning |
|---|---|
| MYRPA2001 | An activity failed: a classified failure (`ActivityFailedException`/`AutomationException`, whose `errorType` such as `ElementNotFound` is reported) or any other exception (`errorType` = exception type name) |
| MYRPA2002 | `Core.Throw` |
| MYRPA2003 | Expression evaluation failed |
| MYRPA2004 | Invalid run arguments |
| MYRPA2005 | Timed out |
| MYRPA2006 | Cancelled |
| MYRPA2007 | Invoked workflow did not succeed |
| MYRPA2008 | Maximum invocation depth exceeded |
| MYRPA2009 | Invoked workflow could not be resolved or is invalid |
| MYRPA2010 | A graph container ran more than `maxSteps` steps (`errorType` `Graph`) |
| MYRPA2011 | A state machine cannot go on: no transition taken from a state that is not final, or a final state has transitions (`errorType` `Graph`) |

## 8. Compatibility policy

- Readers accept the current major version with a minor ≤ their own; newer files are rejected with MYRPA1011.
- Minor versions only add optional fields or activities. Major versions require a migration step (inserted right after
  the schema-version check in `WorkflowLoader`).
- `WorkflowJsonWriter` writes the same format (load → write round-trips) and keeps the file's schema version: a
  workflow without graph data stays `1.0`.
- Version 1.1 (ADR-0037) added `transitions`, `layout`, graph containers, `Core.Flowchart`, `Core.Decision`,
  `Core.StateMachine` and `Core.State`. A 1.0
  reader rejects 1.1 files with MYRPA1011.
