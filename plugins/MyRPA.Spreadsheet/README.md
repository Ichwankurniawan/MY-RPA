# MyRPA.Spreadsheet

Excel workbook activities (Phase 7, [ADR-0042](../../docs/adr/0042-enterprise-automation-activities.md)). A product
plugin built on `DocumentFormat.OpenXml` (MIT), which only this plugin may reference: `.xlsx` files are read and
written directly, Excel is not needed and never started, and nothing in a workbook is executed (formulas give their
last calculated value; macros in `.xlsm` files are ignored). It runs in-process and is fully trusted (ADR-0015); its
load context is not a sandbox.

## Settings (plugin configuration, ADR-0019)

| Setting | Default | Meaning |
|---|---|---|
| `fileRoot` | the host's working directory | The only folder tree the activities use (same policy as MyRPA.Files) |
| `maxFileBytes` | 52428800 (50 MB) | The largest workbook opened; no part may expand beyond ten times this (zip bombs) |
| `maxRows` | 100000 | The most rows read or written |
| `maxCells` | 2000000 | The most non-empty cells (and shared strings) read |

## Activities

| Type | Inputs | Output | Errors |
|---|---|---|---|
| `Excel.GetSheets` | `path` | `result`: the sheet names in order | `FileNotFound`, `InvalidWorkbook` |
| `Excel.ReadRange` | `path`, `sheet` (default the first), `range` (e.g. `A1:D100`, default the used cells), `hasHeader` (default true) | `result`: a List of Dictionaries (header) or of Lists | `SheetNotFound`, `InvalidInput` for a bad range, `TooManyItems` |
| `Excel.WriteRange` | `path`, `sheet` (default `Sheet1`, created when missing), `startCell` (default `A1`), `rows`, `columns`, `writeHeader` (default true), `createFile` (default true) | — | `FileNotFound` (createFile false), `InvalidInput`, `TooManyItems` |
| `Excel.AppendRows` (7.1) | `path`, `sheet` (default `Sheet1`, created when missing), `rows`, `startColumn` (default `A`), `createFile` (default true) | `result`: the row number of the first row added | `InvalidInput` for a column the header lacks, `FileNotFound`, `TooManyItems` |
| `Excel.ClearRange` (7.1) | `path`, `sheet` (default the first), `range` | `result`: the number of cells cleared | `SheetNotFound`, `InvalidInput` for a bad range, `FileNotFound` |

**Reading:** the sheet is streamed row by row and stops after the requested range. Whole numbers become Int and other
numbers Decimal. Booleans become Boolean, and cells with a date or time number format become DateTime (UTC). Errors
such as `#DIV/0!` become their text, and empty cells become null.

**Writing:** the activity changes only the cells it writes; null clears a cell. Text is always written as a text
cell, so `=1+1` stays text and can never become a formula. A DateTime gets a date-and-time format. The workbook is
built in memory and replaces the file in one step, so a failed write leaves the previous file untouched. A formula
cell that is overwritten loses its formula, and Excel rebuilds its calculation chain.

All three activities declare the `FileSystem` side effect in the catalog (1.2). Paths outside `fileRoot`, or through
links, fail with `FileAccessDenied`; `.xls` (the older binary format) and damaged or encrypted files fail with
`InvalidWorkbook`.
