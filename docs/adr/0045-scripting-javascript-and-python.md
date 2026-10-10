# ADR-0045: Scripting activities: sandboxed JavaScript and opt-in Python

- Status: **Accepted** (owner, 2026-10-10: "do both", choosing sandboxed JavaScript and opt-in Python).
- Phase: an extension of Phase 7 (enterprise activities). Amends ADR-0008 (point 6, dynamic code execution) and
  ADR-0009 (Roslyn rejected), without changing either for the engine.
- Builds on: ADR-0013 (the activity contract), ADR-0015 (plugins are fully trusted), ADR-0042/0043 (product plugins,
  catalog 1.2, `FileRootPolicy`).

## Context

Users coming from other RPA tools expect an "Invoke Code" activity, mainly to run JavaScript or Python for logic that
is awkward in activities or expressions: parsing, reshaping data, small calculations, calling a Python library.

ADR-0008 forbids eval-style scripting "without an explicit, sandboxed design ADR". ADR-0009 rejected Roslyn C#
scripting because .NET code cannot be sandboxed in-process. The expression language stays the engine's only
computation language. This ADR adds scripting **as a plugin**, so the engine and its rules do not change.

## Decision

1. **A new plugin, `MyRPA.Scripting`, with two activities.** Operators choose to load it, and nothing in `src` changes
   its behaviour. `Jint` (BSD-2-Clause, a JavaScript interpreter written in .NET) and its parser `Acornima` belong to
   this plugin only (`TechnologyPackageOwners`).
2. **`Code.JavaScript` is sandboxed.** It runs in Jint with:
   - no CLR access (interop never enabled), no `require`, no file, network or process access;
   - limits on time (`timeoutMs`), statements, memory and recursion, and cancellation with the run;
   - inputs that cross the boundary only as JSON: workflow values go in as a parsed `inputs` object, and the returned
     value comes back through `JSON.stringify` and becomes a workflow value.

   No .NET object is ever exposed to a script, which is what makes this the sandbox ADR-0008 asks for.
3. **`Code.Python` is opt-in and not sandboxed.** It runs the operator's installed Python as a separate process:
   - The operator must set `pythonPath`; without it the activity fails (`PythonNotConfigured`), so a workflow alone
     cannot start Python.
   - The script runs isolated from the user's site packages (`python -I`), with a timeout that kills the process tree,
     and limits on the result and output sizes.
   - Inputs go in as JSON on standard input, and the returned value comes back as JSON through a private temporary
     file. The working folder is the plugin's `fileRoot` when set.
   - The script can do anything the robot's account can (files, network, packages). The documentation says so plainly.
4. **The script is literal text**, never an expression (like `Db` SQL, ADR-0043). Workflow data reaches the code only
   through `inputs`, never by being spliced into it.
5. **Catalog: an additive `multiline` flag on text properties** (`ActivityPropertyDefinition.IsMultiline`, JSON
   `"multiline": true`). The Studio shows such a property in a monospace text box, still catalog-driven. Used by
   `code` and by the database `sql`. Old readers ignore the field, and the catalog format stays 1.2.

## Consequences

- The Studio gets JavaScript and Python without per-activity UI code.
- **Error types:**
  - `ScriptSyntax` (the code does not parse);
  - `ScriptError` (the script threw; the message carries its error and line);
  - `ScriptLimit` (statements, memory or recursion);
  - `Timeout`;
  - `InvalidResult` (a result that is not JSON);
  - `PythonNotConfigured`.
- **Tests:**
  - JavaScript: escapes that must fail (no `System`, `require`, `fetch`, `importNamespace`), every limit, values
    round-trip;
  - Python: when an interpreter is found, values round-trip, errors and timeouts (with a killed process).
- Inline C# (Roslyn) stays rejected (ADR-0009). Reusable .NET logic belongs in a plugin activity (the SDK).

## Alternatives considered

- **Roslyn C#:** not sandboxable in-process (ADR-0009).
- **ClearScript or V8:** a native engine, a large dependency, and more to sandbox than Jint.
- **IronPython:** exposes the CLR, so it cannot be sandboxed, and it lags behind Python 3.
- **Python with no operator switch:** any workflow could run arbitrary code; rejected.
