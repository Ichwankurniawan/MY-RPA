# MyRPA.Files

File, folder and data-file activities (Phase 7, [ADR-0042](../../docs/adr/0042-enterprise-automation-activities.md)).
A product plugin: it references only the Automation SDK and no packages, and is loaded by the plugin host like any
other plugin. It runs in-process and is fully trusted (ADR-0015); its load context is not a sandbox.

## Settings (plugin configuration, ADR-0019)

| Setting | Default | Meaning |
|---|---|---|
| `fileRoot` | the host's working directory | The only folder tree the activities read or write |
| `maxFileBytes` | 52428800 (50 MB) | The largest file `File.ReadText`, `Csv.Read`, `Json.ReadFile` and `Xml.ReadFile` read |
| `maxItems` | 10000 | The most entries `File.List` returns |
| `maxRows` | 100000 | The most rows `Csv.Read` returns |

## File policy (`MyRPA.Sdk.Files.FileRootPolicy`)

- Every path is relative to `fileRoot` (or absolute inside it), normalized with `Path.GetFullPath`, and refused
  (`FileAccessDenied`) when it leaves the root, including `..` segments and sibling folders such as `root2`.
- A path that goes through a symbolic link or junction (any reparse point) between the root and the target is refused.
  `File.List` skips links.
- Writes never replace an existing file silently: `File.WriteText`, `File.Copy`, `File.Move`, `Csv.Write` and
  `Json.WriteFile` fail with `FileAlreadyExists` unless `overwrite` is true (or `append` for `File.WriteText`).
- `File.Delete` deletes one file, never a folder. There is no folder delete.
- Error messages name the path as the workflow gave it, never the absolute root.

What this does **not** claim: the check runs when a path is resolved, so a link created inside the root by another
process between the check and the operation is a race it cannot close. The root should be a folder that only the
robot's account can write to. The plugin is not a security boundary against a hostile local user.

## Activities

| Type | Purpose | Errors |
|---|---|---|
| `File.Exists` | Whether a file or folder exists | `FileAccessDenied` outside the root |
| `File.List` | Files or folders matching a pattern, sorted, relative to the root | `TooManyItems`, `InvalidPath` for a pattern with a folder |
| `File.ReadText` / `File.WriteText` | Read or write a text file (utf-8, utf-16, latin1, ascii) | `FileNotFound`, `FileTooLarge`, `FileAlreadyExists`, `FileIoError` |
| `File.Copy` / `File.Move` / `File.Delete` | Files only | `FileNotFound`, `FileAlreadyExists`, `InvalidPath` for a folder |
| `Folder.Create` | Creates a folder and its parents; idempotent | `InvalidPath` when a file has that name |
| `Csv.Read` | RFC 4180 CSV into a List of Dictionaries (header) or Lists | `InvalidCsv` for an unclosed quote, `TooManyItems` |
| `Csv.Write` | A table to CSV; text starting with `= + - @` gets a leading `'` unless `protectFormulas` is false | `FileAlreadyExists`, `InvalidInput` |
| `Json.ReadFile` / `Json.WriteFile` | JSON files as workflow values; nesting deeper than 64 is refused | `InvalidJson` |
| `Xml.ReadFile` | XML into `{ name, attributes, text, children }`; DTDs and external entities refused, depth 64 | `InvalidXml` |

All activities declare the `FileSystem` side effect in the catalog (1.2). Operating-system failures become
`FileIoError` with a message that does not repeat the system's text (which can contain absolute paths).
