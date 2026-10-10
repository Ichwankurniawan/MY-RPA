# MyRPA.Scripting

JavaScript and Python inside workflows ([ADR-0045](../../docs/adr/0045-scripting-javascript-and-python.md)). A product
plugin: it references the Automation SDK and [Jint](https://github.com/sebastienros/jint) 4.17.0 (BSD-2-Clause, a
JavaScript interpreter written in .NET), which only this plugin may reference. It runs in-process and is fully trusted
(ADR-0015); its load context is not a sandbox. The JavaScript sandbox below comes from Jint, which never gives a script
any .NET object.

## Activities

Both take the same properties:

| Property | Kind | Meaning |
|---|---|---|
| `code` | text, multiline, **never an expression** | The script, written as a function body that reads `inputs` and ends with `return value` |
| `inputs` | expression, Dictionary | The values the script reads, such as `{ 'lines': lines, 'rate': 0.2 }` |
| `timeoutMs` | expression, Int | 30000 for JavaScript, 60000 for Python; capped by `maxTimeoutMs` and the run's deadline |
| `result` | assignment target | The returned value as a workflow value; null when nothing is returned |

Values cross as JSON both ways: texts, numbers, true/false, null, Lists and Dictionaries; dates arrive as ISO text.

### `Code.JavaScript`: confined, no access to the computer

```js
const invoices = inputs.lines.filter(l => l.startsWith('INV')).map(l => l.split(';'));
return { count: invoices.length, numbers: invoices.map(i => i[0]) };
```

The script has standard JavaScript (ES2023: strings, arrays, `JSON`, `Math`, `Date`, regular expressions). It has no
`require`, `fetch`, files, network, processes or .NET, and `eval` and `new Function` are refused. Limits stop a runaway
script:
- time: `timeoutMs`;
- statements: `maxStatements`;
- memory: `maxMemoryBytes`;
- call depth: `maxRecursion`.

It also stops when the run is cancelled.

### `Code.Python`: opt-in, not a sandbox

```python
invoices = [l.split(';') for l in inputs['lines'] if l.startswith('INV')]
print('found', len(invoices))
return {'count': len(invoices), 'numbers': [n for n, _ in invoices]}
```

It runs only when the operator names an interpreter in `pythonPath`; without it the activity fails with
`PythonNotConfigured`, so a workflow alone cannot start Python. The script runs:
- in a separate process with `python -I` (the user's site packages and environment variables for Python are ignored);
- in the plugin's `fileRoot` as its working folder (a private temporary folder otherwise);
- with a timeout that kills the process and its children.

What it prints goes to `output`. Indent with spaces. **Python is not sandboxed:** a script can read and write files, use
the network and import installed packages, with the rights of the robot's account. Enable it only where whoever edits
workflows may run code on the robot.

## Settings (plugin configuration, ADR-0019)

| Setting | Default | Meaning |
|---|---|---|
| `pythonPath` | not set | The Python interpreter (such as `C:\Python312\python.exe` or `/usr/bin/python3`); not set, `Code.Python` is refused |
| `fileRoot` | not set | The working folder of Python scripts |
| `maxTimeoutMs` | 300000 | The longest timeout a script may ask for |
| `maxStatements` | 10000000 | The most JavaScript statements one script runs |
| `maxMemoryBytes` | 268435456 (256 MB) | The most memory one JavaScript run may allocate |
| `maxRecursion` | 500 | The deepest JavaScript call depth |
| `maxResultBytes` | 10485760 (10 MB) | The largest inputs and result (as JSON) |
| `maxOutputBytes` | 1048576 (1 MB) | The most printed output kept from a Python run |

| errorType | When |
|---|---|
| `ScriptSyntax` | The code does not parse (the message names the line in your code) |
| `ScriptError` | The script threw an error (its message and line) |
| `ScriptLimit` | Past the statement, memory or recursion limit (the message names the setting) |
| `Timeout` | Ran longer than `timeoutMs` |
| `InvalidResult` | The result cannot become a workflow value, or is larger than `maxResultBytes` |
| `PythonNotConfigured` | `Code.Python` without `pythonPath` |

## Tests

`tests/MyRPA.Scripting.Tests` covers:
- JavaScript, always: values, no way out to .NET, `eval` or the host, each limit, syntax and runtime errors with lines;
- Python, with an interpreter on PATH or `MYRPA_TEST_PYTHON`, otherwise skipped: values, printed output, errors with
  lines, the timeout killing the process, and refusal without `pythonPath`.
