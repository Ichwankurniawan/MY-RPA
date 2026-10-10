using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MyRPA.Core.Activities;
using MyRPA.Sdk.Files;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;

namespace MyRPA.Spreadsheet;

/// <summary><c>Excel.GetSheets</c>: the names of a workbook's sheets.</summary>
public sealed class ExcelGetSheetsActivity(SpreadsheetOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Excel.GetSheets"),
        "Get Sheets",
        "Excel",
        "The names of a workbook's sheets, in order. Reads .xlsx (and .xlsm) files directly; Excel is not needed.",
        [ExcelProperties.PathInput(), ExcelProperties.Output(ActivityValueType.List, "Receives the sheet names (a List of String).")])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = ExcelProperties.Text(context, "path");
        var full = Workbooks.ExistingWorkbook(options, path);
        var names = Workbooks.Use(path, () =>
        {
            using var document = SpreadsheetDocument.Open(full, false, Workbooks.Settings(options));
            return Workbooks.SheetNames(document.WorkbookPart ?? throw new InvalidDataException("No workbook part."));
        });
        ExcelProperties.SetResult(context, WorkflowValues.List(names));
        return ActivityResult.CompletedTask;
    }
}

/// <summary><c>Excel.ReadRange</c>: a range of a worksheet as a table.</summary>
public sealed class ExcelReadRangeActivity(SpreadsheetOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Excel.ReadRange"),
        "Read Range",
        "Excel",
        "Reads a range of a worksheet: with a header, a List of Dictionaries keyed by the first row; without, a List of Lists. Numbers become Int or Decimal, booleans Boolean, cells with a date format DateTime, empty cells null; formulas give their last calculated value. The sheet is read row by row; fails with TooManyItems above the plugin's maxRows or maxCells.",
        [
            ExcelProperties.PathInput(),
            ExcelProperties.Input("sheet", ActivityValueType.String, "The worksheet's name (default: the first sheet)."),
            ExcelProperties.Input("range", ActivityValueType.String, "A range such as 'A1:D100' (default: the sheet's used cells)."),
            ExcelProperties.Input("hasHeader", ActivityValueType.Boolean, "The first row names the columns.", defaultJson: "true"),
            ExcelProperties.Output(ActivityValueType.List, "Receives the rows."),
        ])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = ExcelProperties.Text(context, "path");
        var sheet = ExcelProperties.OptionalText(context, "sheet");
        CellRange? range = null;
        if (ExcelProperties.OptionalText(context, "range") is { } rangeText)
        {
            range = CellAddress.TryParseRange(rangeText.Trim(), out var parsed)
                ? parsed
                : throw new ActivityFailedException(SpreadsheetErrorTypes.InvalidInput, $"'range' of {context.Node.Type} '{context.Node.Id}' must be a range such as A1:D100.");
        }

        var hasHeader = ExcelProperties.Flag(context, "hasHeader", true);
        var full = Workbooks.ExistingWorkbook(options, path);
        var rows = Workbooks.Use(path, () =>
        {
            using var document = SpreadsheetDocument.Open(full, false, Workbooks.Settings(options));
            var workbook = document.WorkbookPart ?? throw new InvalidDataException("No workbook part.");
            var part = Workbooks.Worksheet(workbook, sheet, path);
            var cells = Workbooks.ReadCells(part, Workbooks.SharedStrings(workbook, options, context.CancellationToken), Workbooks.DateStyles(workbook), range, options, path, context.CancellationToken);
            return Shape(cells, range, hasHeader, path);
        });
        ExcelProperties.SetResult(context, WorkflowValues.List(rows));
        return ActivityResult.CompletedTask;
    }

    private List<object?> Shape(SortedDictionary<int, SortedDictionary<int, object?>> cells, CellRange? range, bool hasHeader, string path)
    {
        if (range is null && cells.Count == 0)
        {
            return [];
        }

        var bounds = range ?? new CellRange(cells.Keys.First(), cells.Values.Min(r => r.Keys.First()), cells.Keys.Last(), cells.Values.Max(r => r.Keys.Last()));
        var firstData = bounds.FirstRow + (hasHeader ? 1 : 0);
        if ((long)bounds.LastRow - firstData + 1 > options.MaxRows)
        {
            throw Workbooks.TooManyRows(path, options);
        }

        object? At(int row, int column) => cells.TryGetValue(row, out var values) && values.TryGetValue(column, out var value) ? value : null;
        var columns = Enumerable.Range(bounds.FirstColumn, bounds.LastColumn - bounds.FirstColumn + 1).ToList();
        var header = hasHeader ? ExcelProperties.Header([.. columns.Select(c => At(bounds.FirstRow, c) is { } v ? WorkflowValues.ToDisplayString(v) : string.Empty)]) : null;
        var result = new List<object?>();
        for (var row = firstData; row <= bounds.LastRow; row++)
        {
            var r = row;
            result.Add(header is null
                ? WorkflowValues.List(columns.Select(c => At(r, c)))
                : WorkflowValues.Dictionary(columns.Select((c, i) => new KeyValuePair<string, object?>(header[i], At(r, c)))));
        }

        return result;
    }
}

/// <summary><c>Excel.WriteRange</c>: a table into a worksheet.</summary>
public sealed class ExcelWriteRangeActivity(SpreadsheetOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Excel.WriteRange"),
        "Write Range",
        "Excel",
        "Writes a table (a List of Dictionaries, or of Lists) into a worksheet from a start cell, creating the workbook (with createFile) or the sheet when missing. Only the written cells change; null clears a cell. Text is always written as text, never as a formula. The file is replaced in one step, so a failure leaves it as it was.",
        [
            ExcelProperties.PathInput(),
            ExcelProperties.Input("sheet", ActivityValueType.String, "The worksheet's name (created when missing).", defaultJson: "\"Sheet1\""),
            ExcelProperties.Input("startCell", ActivityValueType.String, "The top-left cell.", defaultJson: "\"A1\""),
            ExcelProperties.Input("rows", ActivityValueType.List, "The rows.", required: true),
            ExcelProperties.Input("columns", ActivityValueType.List, "The column names, in order (default: the keys of the rows, in the order first seen)."),
            ExcelProperties.Input("writeHeader", ActivityValueType.Boolean, "Write the column names first (rows that are Dictionaries).", defaultJson: "true"),
            ExcelProperties.Input("createFile", ActivityValueType.Boolean, "Create the workbook when it does not exist (otherwise that fails with FileNotFound).", defaultJson: "true"),
        ])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = ExcelProperties.Text(context, "path");
        var sheet = ExcelProperties.SheetName(context);

        var startText = (ExcelProperties.OptionalText(context, "startCell") ?? "A1").Trim();
        if (!CellAddress.TryParse(startText, out var startColumn, out var startRow))
        {
            throw new ActivityFailedException(SpreadsheetErrorTypes.InvalidInput, $"'startCell' of {context.Node.Type} '{context.Node.Id}' must be a cell such as A1.");
        }

        var table = ExcelProperties.Table(context, ExcelProperties.Flag(context, "writeHeader", true));
        if (table.Count > options.MaxRows + 1)
        {
            throw new ActivityFailedException(FileErrorTypes.TooManyItems, $"More than {options.MaxRows} rows to write (setting maxRows).");
        }

        if (startRow + table.Count - 1 > CellAddress.MaxRow || startColumn + table.Select(r => r.Count).DefaultIfEmpty(0).Max() - 1 > CellAddress.MaxColumn)
        {
            throw new ActivityFailedException(SpreadsheetErrorTypes.InvalidInput, "The rows do not fit in a worksheet from that start cell.");
        }

        var (full, exists) = ExcelProperties.WorkbookToWrite(context, options, path);

        // Built in memory and then moved over the file in one step: a failure never leaves a half-written workbook.
        Workbooks.Use(path, () => Workbooks.Rewrite(full, exists, options, workbook =>
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            Workbooks.WriteCells(workbook, sheet, startRow, startColumn, table);
            return true;
        }));
        return ActivityResult.CompletedTask;
    }
}

/// <summary>Property declarations and checks shared by the Excel activities.</summary>
internal static class ExcelProperties
{
    public static ActivityPropertyDefinition Input(string name, ActivityValueType type, string description, bool required = false, string? defaultJson = null) =>
        new(name, ActivityPropertyKind.Expression, required, description) { ValueType = type, DefaultValue = defaultJson };

    public static ActivityPropertyDefinition PathInput() =>
        Input("path", ActivityValueType.String, "The workbook (.xlsx), relative to the plugin's file root (or absolute inside it).", required: true);

    public static ActivityPropertyDefinition Output(ActivityValueType type, string description) =>
        new("result", ActivityPropertyKind.AssignmentTarget, isRequired: true, description) { ValueType = type };

    public static string Text(IActivityContext context, string name) =>
        context.Evaluate(name) is string text ? text : throw Invalid(context, name, "text");

    public static string? OptionalText(IActivityContext context, string name) => context.HasProperty(name) ? Text(context, name) : null;

    public static bool Flag(IActivityContext context, string name, bool defaultValue) =>
        !context.HasProperty(name) ? defaultValue : context.Evaluate(name) is bool b ? b : throw Invalid(context, name, "true or false");

    public static void SetResult(IActivityContext context, object? value) => context.SetValue(context.GetName("result"), value);

    /// <summary>The <c>sheet</c> property as a valid worksheet name (default Sheet1).</summary>
    public static string SheetName(IActivityContext context)
    {
        var sheet = OptionalText(context, "sheet") ?? "Sheet1";
        return sheet.Length is 0 or > 31 || sheet.IndexOfAny(['[', ']', ':', '*', '?', '/', '\\']) >= 0 || sheet.StartsWith('\'') || sheet.EndsWith('\'')
            ? throw new ActivityFailedException(SpreadsheetErrorTypes.InvalidInput, $"'sheet' of {context.Node.Type} '{context.Node.Id}' must be 1 to 31 characters without [ ] : * ? / \\.")
            : sheet;
    }

    /// <summary>The workbook to change: an existing one, or a new one when <c>createFile</c> (default true) allows it.</summary>
    public static (string Full, bool Exists) WorkbookToWrite(IActivityContext context, SpreadsheetOptions options, string path)
    {
        var exists = File.Exists(options.Files.Resolve(path));
        Workbooks.CheckExtension(path, allowMacroEnabled: exists);
        if (exists)
        {
            return (Workbooks.ExistingWorkbook(options, path), true);
        }

        return Flag(context, "createFile", true)
            ? (options.Files.ResolveFileToWrite(path, overwrite: false), false)
            : throw new ActivityFailedException(FileErrorTypes.FileNotFound, $"The workbook '{path}' does not exist (createFile is false).");
    }

    public static ActivityFailedException Invalid(IActivityContext context, string name, string expected) =>
        new(SpreadsheetErrorTypes.InvalidInput, $"'{name}' of {context.Node.Type} '{context.Node.Id}' must be {expected}.");

    /// <summary>Header names made unique (a blank or repeated name becomes column{n}).</summary>
    public static List<string> Header(List<string> names)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i].Trim();
            if (name.Length == 0 || !used.Add(name))
            {
                name = $"column{i + 1}";
                used.Add(name);
            }

            result.Add(name);
        }

        return result;
    }

    /// <summary>The rows as a grid: a header row first for Dictionaries (when <paramref name="writeHeader"/>), then values.</summary>
    public static List<List<object?>> Table(IActivityContext context, bool writeHeader)
    {
        var rows = context.Evaluate("rows") is IReadOnlyList<object?> list and not IReadOnlyDictionary<string, object?> ? list : throw Invalid(context, "rows", "a list of rows");
        var tables = rows.All(r => r is IReadOnlyDictionary<string, object?>);
        if (!tables && !rows.All(r => r is IReadOnlyList<object?> and not IReadOnlyDictionary<string, object?>))
        {
            throw Invalid(context, "rows", "a list of Dictionaries or a list of Lists");
        }

        List<string> columns = [];
        if (context.HasProperty("columns"))
        {
            columns = context.Evaluate("columns") is IReadOnlyList<object?> names && names.All(n => n is string)
                ? [.. names.Cast<string>()]
                : throw Invalid(context, "columns", "a list of column names");
        }
        else if (tables)
        {
            columns = [.. rows.Cast<IReadOnlyDictionary<string, object?>>().SelectMany(r => r.Keys).Distinct(StringComparer.Ordinal)];
        }

        var grid = new List<List<object?>>();
        if (tables && writeHeader)
        {
            grid.Add([.. columns]);
        }

        foreach (var row in rows)
        {
            grid.Add(row is IReadOnlyDictionary<string, object?> d
                ? [.. columns.Select(c => d.TryGetValue(c, out var v) ? v : null)]
                : [.. (IReadOnlyList<object?>)row!]);
        }

        return grid;
    }
}

/// <summary>
/// <c>Excel.AppendRows</c>: adds rows after the last used row of a worksheet (ADR-0043). Rows that are Dictionaries are
/// matched to the sheet's existing header row by name; on an empty sheet the header is written first.
/// </summary>
public sealed class ExcelAppendRowsActivity(SpreadsheetOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Excel.AppendRows"),
        "Append Rows",
        "Excel",
        "Adds rows below the last used row of a worksheet, creating the workbook (with createFile) or the sheet when missing. Rows that are Dictionaries go under the sheet's header row by column name (a name the header lacks fails); on an empty sheet the header is written first. Text is never a formula; the file is replaced in one step.",
        [
            ExcelProperties.PathInput(),
            ExcelProperties.Input("sheet", ActivityValueType.String, "The worksheet's name (created when missing).", defaultJson: "\"Sheet1\""),
            ExcelProperties.Input("rows", ActivityValueType.List, "The rows: a List of Dictionaries (by column name) or of Lists (by position).", required: true),
            ExcelProperties.Input("startColumn", ActivityValueType.String, "The first column of the table (its header row starts there).", defaultJson: "\"A\""),
            ExcelProperties.Input("createFile", ActivityValueType.Boolean, "Create the workbook when it does not exist (otherwise that fails with FileNotFound).", defaultJson: "true"),
            new("result", ActivityPropertyKind.AssignmentTarget, isRequired: false, "Receives the row number of the first row added.") { ValueType = ActivityValueType.Int },
        ])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = ExcelProperties.Text(context, "path");
        var sheet = ExcelProperties.SheetName(context);
        var columnText = (ExcelProperties.OptionalText(context, "startColumn") ?? "A").Trim();
        if (!CellAddress.TryParse(columnText + "1", out var startColumn, out _))
        {
            throw ExcelProperties.Invalid(context, "startColumn", "a column such as A");
        }

        var rows = context.Evaluate("rows") is IReadOnlyList<object?> list and not IReadOnlyDictionary<string, object?> ? list : throw ExcelProperties.Invalid(context, "rows", "a list of rows");
        var dictionaries = rows.All(r => r is IReadOnlyDictionary<string, object?>);
        if (!dictionaries && !rows.All(r => r is IReadOnlyList<object?> and not IReadOnlyDictionary<string, object?>))
        {
            throw ExcelProperties.Invalid(context, "rows", "a list of Dictionaries or a list of Lists");
        }

        if (rows.Count > options.MaxRows)
        {
            throw new ActivityFailedException(FileErrorTypes.TooManyItems, $"More than {options.MaxRows} rows to write (setting maxRows).");
        }

        var (full, exists) = ExcelProperties.WorkbookToWrite(context, options, path);
        var first = Workbooks.Use(path, () => Workbooks.Rewrite(full, exists, options, workbook =>
        {
            var part = Workbooks.SheetForWriting(workbook, sheet);
            var (firstUsed, lastUsed) = Workbooks.UsedRows(part);
            var table = new List<List<object?>>();
            if (dictionaries)
            {
                List<string> columns = firstUsed == 0
                    ? [.. rows.Cast<IReadOnlyDictionary<string, object?>>().SelectMany(r => r.Keys).Distinct(StringComparer.Ordinal)]
                    : Header(workbook, part, firstUsed, startColumn, path, context.CancellationToken);
                if (firstUsed == 0)
                {
                    table.Add([.. columns]);
                }

                foreach (var row in rows.Cast<IReadOnlyDictionary<string, object?>>())
                {
                    if (row.Keys.FirstOrDefault(k => !columns.Contains(k, StringComparer.Ordinal)) is { } unknown)
                    {
                        throw new ActivityFailedException(SpreadsheetErrorTypes.InvalidInput, $"A row has the column '{unknown}', which the header of '{sheet}' does not have.");
                    }

                    table.Add([.. columns.Select(c => row.TryGetValue(c, out var v) ? v : null)]);
                }
            }
            else
            {
                table.AddRange(rows.Select(r => ((IReadOnlyList<object?>)r!).ToList()));
            }

            var startRow = lastUsed + 1;
            if (startRow + table.Count - 1 > CellAddress.MaxRow || startColumn + table.Select(r => r.Count).DefaultIfEmpty(0).Max() - 1 > CellAddress.MaxColumn)
            {
                throw new ActivityFailedException(SpreadsheetErrorTypes.InvalidInput, "The rows do not fit in the worksheet.");
            }

            context.CancellationToken.ThrowIfCancellationRequested();
            Workbooks.WriteCells(workbook, part, startRow, startColumn, table);
            return firstUsed == 0 && dictionaries ? startRow + 1 : startRow;
        }));
        if (context.HasProperty("result"))
        {
            context.SetValue(context.GetName("result"), (long)first);
        }

        return ActivityResult.CompletedTask;
    }

    /// <summary>The names in the header row (the first used row), from the start column to the last named column.</summary>
    private List<string> Header(WorkbookPart workbook, WorksheetPart part, int row, int startColumn, string path, CancellationToken cancellationToken)
    {
        var cells = Workbooks.ReadCells(part, Workbooks.SharedStrings(workbook, options, cancellationToken), Workbooks.DateStyles(workbook), new CellRange(row, startColumn, row, CellAddress.MaxColumn), options, path, cancellationToken);
        var values = cells.TryGetValue(row, out var found) ? found : [];
        var last = values.Count == 0 ? startColumn - 1 : values.Keys.Max();
        return ExcelProperties.Header([.. Enumerable.Range(startColumn, last - startColumn + 1).Select(c => values.TryGetValue(c, out var v) && v is not null ? WorkflowValues.ToDisplayString(v) : string.Empty)]);
    }
}

/// <summary><c>Excel.ClearRange</c>: clears the values and formulas of a range; formatting stays (ADR-0043).</summary>
public sealed class ExcelClearRangeActivity(SpreadsheetOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Excel.ClearRange"),
        "Clear Range",
        "Excel",
        "Clears the values and formulas of the cells in a range of an existing worksheet; their formatting stays. The file is replaced in one step.",
        [
            ExcelProperties.PathInput(),
            ExcelProperties.Input("sheet", ActivityValueType.String, "The worksheet's name (default: the first sheet)."),
            ExcelProperties.Input("range", ActivityValueType.String, "A range such as 'A2:D100'.", required: true),
            new("result", ActivityPropertyKind.AssignmentTarget, isRequired: false, "Receives the number of cells cleared.") { ValueType = ActivityValueType.Int },
        ])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = ExcelProperties.Text(context, "path");
        var sheet = ExcelProperties.OptionalText(context, "sheet");
        var rangeText = ExcelProperties.Text(context, "range").Trim();
        if (!CellAddress.TryParseRange(rangeText, out var range))
        {
            throw ExcelProperties.Invalid(context, "range", "a range such as A1:D100");
        }

        var full = Workbooks.ExistingWorkbook(options, path);
        var cleared = Workbooks.Use(path, () => Workbooks.Rewrite(full, exists: true, options, workbook =>
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            return Workbooks.ClearCells(workbook, Workbooks.Worksheet(workbook, sheet, path), range);
        }));
        if (context.HasProperty("result"))
        {
            context.SetValue(context.GetName("result"), (long)cleared);
        }

        return ActivityResult.CompletedTask;
    }
}
