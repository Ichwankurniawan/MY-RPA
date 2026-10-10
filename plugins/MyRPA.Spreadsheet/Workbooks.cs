using System.Globalization;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MyRPA.Sdk.Files;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;

namespace MyRPA.Spreadsheet;

/// <summary>Opening, reading and writing workbooks with the Open XML SDK; Excel itself is never used.</summary>
internal static class Workbooks
{
    /// <summary>Package settings: no part may expand beyond ten times the file limit (zip bombs).</summary>
    public static OpenSettings Settings(SpreadsheetOptions options) => new() { MaxCharactersInPart = options.MaxFileBytes * 10 };

    /// <summary>An existing workbook inside the root, not larger than the limit, with a workbook extension.</summary>
    public static string ExistingWorkbook(SpreadsheetOptions options, string path)
    {
        var full = options.Files.ResolveExistingFile(path);
        CheckExtension(path, allowMacroEnabled: true);
        return new FileInfo(full).Length <= options.MaxFileBytes
            ? full
            : throw new ActivityFailedException(FileErrorTypes.FileTooLarge, $"The workbook '{path}' is larger than the limit of {options.MaxFileBytes} bytes (setting maxFileBytes).");
    }

    public static void CheckExtension(string path, bool allowMacroEnabled)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) && !(allowMacroEnabled && extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ActivityFailedException(SpreadsheetErrorTypes.InvalidWorkbook, $"'{path}' is not an .xlsx workbook (the older .xls format and other files are not supported).");
        }
    }

    /// <summary>Runs an action on a workbook; package and XML failures become InvalidWorkbook, I/O ones FileIoError.</summary>
    public static T Use<T>(string path, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or FileFormatException or XmlException or InvalidOperationException)
        {
            throw new ActivityFailedException(SpreadsheetErrorTypes.InvalidWorkbook, $"'{path}' is not an .xlsx workbook this activity can read (it may be damaged or encrypted).", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ActivityFailedException(FileErrorTypes.FileIoError, $"Cannot use '{path}': it is in use, read-only or not accessible.", ex);
        }
    }

    public static IReadOnlyList<string> SheetNames(WorkbookPart workbook) =>
        [.. workbook.Workbook?.Sheets?.Elements<Sheet>().Select(s => s.Name?.Value ?? string.Empty) ?? []];

    /// <summary>The worksheet with this name (case-insensitive, like Excel), or the first sheet when no name is given.</summary>
    public static WorksheetPart Worksheet(WorkbookPart workbook, string? name, string path)
    {
        var sheets = workbook.Workbook?.Sheets?.Elements<Sheet>().ToList() ?? [];
        var sheet = name is null ? sheets.FirstOrDefault() : sheets.FirstOrDefault(s => string.Equals(s.Name?.Value, name, StringComparison.OrdinalIgnoreCase));
        return sheet?.Id?.Value is { } id && workbook.GetPartById(id) is WorksheetPart part
            ? part
            : throw new ActivityFailedException(SpreadsheetErrorTypes.SheetNotFound, name is null ? $"'{path}' has no worksheet." : $"'{path}' has no worksheet named '{name}'.");
    }

    /// <summary>The shared strings, read element by element.</summary>
    public static List<string> SharedStrings(WorkbookPart workbook, SpreadsheetOptions options, CancellationToken cancellationToken)
    {
        var strings = new List<string>();
        if (workbook.SharedStringTablePart is not { } part)
        {
            return strings;
        }

        using var reader = OpenXmlReader.Create(part);
        reader.Read();
        while (!reader.EOF)
        {
            if (reader.ElementType == typeof(SharedStringItem) && reader.IsStartElement)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (strings.Count == options.MaxCells)
                {
                    throw TooManyCells(options);
                }

                strings.Add(reader.LoadCurrentElement()?.InnerText ?? string.Empty);
            }
            else
            {
                reader.Read();
            }
        }

        return strings;
    }

    /// <summary>The cell formats (by index) whose number format shows a date or time.</summary>
    public static HashSet<uint> DateStyles(WorkbookPart workbook)
    {
        var dates = new HashSet<uint>();
        if (workbook.WorkbookStylesPart?.Stylesheet is not { } styles)
        {
            return dates;
        }

        var custom = new Dictionary<uint, string>();
        foreach (var format in styles.NumberingFormats?.Elements<NumberingFormat>() ?? [])
        {
            if (format.NumberFormatId?.Value is { } id)
            {
                custom.TryAdd(id, format.FormatCode?.Value ?? string.Empty);
            }
        }

        uint index = 0;
        foreach (var format in styles.CellFormats?.Elements<CellFormat>() ?? [])
        {
            var id = format.NumberFormatId?.Value ?? 0;
            if (id is (>= 14 and <= 22) or (>= 45 and <= 47) || (custom.TryGetValue(id, out var code) && IsDateFormat(code)))
            {
                dates.Add(index);
            }

            index++;
        }

        return dates;
    }

    /// <summary>Whether a custom number format shows a date or time (letters d m y h s outside quotes and brackets).</summary>
    public static bool IsDateFormat(string code)
    {
        var quoted = false;
        var bracket = false;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && c == '[')
            {
                bracket = true;
            }
            else if (bracket && c == ']')
            {
                bracket = false;
            }
            else if (!quoted && !bracket && c == '\\')
            {
                i++;
            }
            else if (!quoted && !bracket && "dmyhsDMYHS".Contains(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The non-empty cells of a worksheet (within <paramref name="range"/> when given), streamed row by row: row → column →
    /// value. Formulas give their cached value; nothing is evaluated.
    /// </summary>
    public static SortedDictionary<int, SortedDictionary<int, object?>> ReadCells(
        WorksheetPart part, List<string> sharedStrings, HashSet<uint> dateStyles, CellRange? range, SpreadsheetOptions options, string path, CancellationToken cancellationToken)
    {
        var rows = new SortedDictionary<int, SortedDictionary<int, object?>>();
        var cells = 0;
        var lastRow = 0;
        using var reader = OpenXmlReader.Create(part);
        reader.Read();
        while (!reader.EOF)
        {
            if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
            {
                reader.Read();
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var row = (Row)reader.LoadCurrentElement()!;
            var rowIndex = row.RowIndex?.Value is { } r ? (int)r : lastRow + 1;
            lastRow = rowIndex;
            if (range is { } bounds && rowIndex > bounds.LastRow)
            {
                break;
            }

            if (range is { } start && rowIndex < start.FirstRow)
            {
                continue;
            }

            var lastColumn = 0;
            foreach (var cell in row.Elements<Cell>())
            {
                var column = cell.CellReference?.Value is { } reference && CellAddress.TryParse(reference, out var c, out _) ? c : lastColumn + 1;
                lastColumn = column;
                if (range is { } b && (column < b.FirstColumn || column > b.LastColumn))
                {
                    continue;
                }

                var value = Value(cell, sharedStrings, dateStyles, path);
                if (value is null)
                {
                    continue;
                }

                if (++cells > options.MaxCells)
                {
                    throw TooManyCells(options);
                }

                if (!rows.TryGetValue(rowIndex, out var values))
                {
                    if (rows.Count > options.MaxRows)
                    {
                        throw TooManyRows(path, options);
                    }

                    rows[rowIndex] = values = [];
                }

                values[column] = value;
            }
        }

        return rows;
    }

    private static object? Value(Cell cell, List<string> sharedStrings, HashSet<uint> dateStyles, string path)
    {
        var raw = cell.CellValue?.Text;
        var type = cell.DataType?.Value;
        if (type == CellValues.SharedString)
        {
            return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < sharedStrings.Count
                ? sharedStrings[index]
                : throw new ActivityFailedException(SpreadsheetErrorTypes.InvalidWorkbook, $"'{path}' has a cell that refers to a missing shared string.");
        }

        if (type == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText;
        }

        if (type == CellValues.Boolean)
        {
            return raw == "1";
        }

        if (type == CellValues.String || type == CellValues.Error)
        {
            return raw;
        }

        if (type == CellValues.Date)
        {
            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : raw;
        }

        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (cell.StyleIndex?.Value is { } style && dateStyles.Contains(style)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is >= -657_435 and < 2_958_466)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(DateTime.FromOADate(serial), DateTimeKind.Utc));
        }

        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number == decimal.Truncate(number) && number is >= long.MinValue and <= long.MaxValue ? (object)(long)number : number;
        }

        return raw;
    }

    public static ActivityFailedException TooManyRows(string path, SpreadsheetOptions options) =>
        new(FileErrorTypes.TooManyItems, $"'{path}' has more than {options.MaxRows} rows to read (setting maxRows); give a smaller range.");

    private static ActivityFailedException TooManyCells(SpreadsheetOptions options) =>
        new(FileErrorTypes.TooManyItems, $"The workbook has more than {options.MaxCells} cells to read (setting maxCells); give a smaller range.");

    /// <summary>Writes a table into a worksheet (created when missing), starting at a cell; only the written cells change.</summary>
    public static void WriteCells(WorkbookPart workbook, string sheetName, int startRow, int startColumn, List<List<object?>> table) =>
        WriteCells(workbook, SheetForWriting(workbook, sheetName), startRow, startColumn, table);

    /// <summary>The worksheet named <paramref name="sheetName"/> (case-insensitive), added when missing.</summary>
    public static WorksheetPart SheetForWriting(WorkbookPart workbook, string sheetName)
    {
        workbook.Workbook ??= new Workbook();
        var sheets = workbook.Workbook.Sheets ?? workbook.Workbook.AppendChild(new Sheets());
        var sheet = sheets.Elements<Sheet>().FirstOrDefault(s => string.Equals(s.Name?.Value, sheetName, StringComparison.OrdinalIgnoreCase));
        WorksheetPart part;
        if (sheet is null)
        {
            part = workbook.AddNewPart<WorksheetPart>();
            part.Worksheet = new Worksheet(new SheetData());
            var nextId = sheets.Elements<Sheet>().Select(s => s.SheetId?.Value ?? 0).DefaultIfEmpty(0u).Max() + 1;
            sheets.Append(new Sheet { Id = workbook.GetIdOfPart(part), SheetId = nextId, Name = sheetName });
        }
        else
        {
            part = workbook.GetPartById(sheet.Id!.Value!) as WorksheetPart
                ?? throw new ActivityFailedException(SpreadsheetErrorTypes.SheetNotFound, $"'{sheetName}' is a chart sheet, not a worksheet.");
        }

        part.Worksheet ??= new Worksheet(new SheetData());
        _ = part.Worksheet.GetFirstChild<SheetData>() ?? part.Worksheet.AppendChild(new SheetData());
        return part;
    }

    /// <summary>Writes a table into a worksheet from a start cell; only the written cells change.</summary>
    public static void WriteCells(WorkbookPart workbook, WorksheetPart part, int startRow, int startColumn, List<List<object?>> table)
    {
        var data = part.Worksheet!.GetFirstChild<SheetData>()!;
        var rows = data.Elements<Row>().ToList();
        uint previous = 0;
        foreach (var existing in rows)
        {
            existing.RowIndex ??= previous + 1;
            previous = existing.RowIndex.Value;
        }

        uint? dateStyle = null;
        var formulasRemoved = false;
        for (var i = 0; i < table.Count; i++)
        {
            var row = RowAt(data, rows, (uint)(startRow + i));
            for (var j = 0; j < table[i].Count; j++)
            {
                formulasRemoved |= SetCell(workbook, row, startColumn + j, table[i][j], ref dateStyle);
            }
        }

        if (formulasRemoved && workbook.CalculationChainPart is { } chain)
        {
            // Excel rebuilds the calculation chain; a stale one names cells that no longer hold formulas.
            workbook.DeletePart(chain);
        }
    }

    /// <summary>The first and last rows that hold a value (0 and 0 for an empty sheet).</summary>
    public static (int First, int Last) UsedRows(WorksheetPart part)
    {
        int first = 0, last = 0, previous = 0;
        foreach (var row in part.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>() ?? [])
        {
            var index = row.RowIndex?.Value is { } r ? (int)r : previous + 1;
            previous = index;
            if (row.Elements<Cell>().Any(c => c.CellValue is not null || c.InlineString is not null || c.CellFormula is not null))
            {
                first = first == 0 ? index : first;
                last = index;
            }
        }

        return (first, last);
    }

    /// <summary>Clears the values and formulas of the cells in a range; formatting stays. Returns the number of cells cleared.</summary>
    public static int ClearCells(WorkbookPart workbook, WorksheetPart part, CellRange range)
    {
        var cleared = 0;
        var formulasRemoved = false;
        var previous = 0;
        foreach (var row in part.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>() ?? [])
        {
            var index = row.RowIndex?.Value is { } r ? (int)r : previous + 1;
            previous = index;
            if (index < range.FirstRow || index > range.LastRow)
            {
                continue;
            }

            var lastColumn = 0;
            foreach (var cell in row.Elements<Cell>().ToList())
            {
                var column = cell.CellReference?.Value is { } text && CellAddress.TryParse(text, out var c, out _) ? c : lastColumn + 1;
                lastColumn = column;
                if (column < range.FirstColumn || column > range.LastColumn)
                {
                    continue;
                }

                if (cell.CellValue is null && cell.InlineString is null && cell.CellFormula is null)
                {
                    continue;
                }

                formulasRemoved |= cell.CellFormula is not null;
                cell.CellFormula?.Remove();
                cell.CellValue?.Remove();
                cell.InlineString?.Remove();
                cell.DataType = null;
                cell.CellReference ??= CellAddress.ColumnName(column) + index.ToString(CultureInfo.InvariantCulture);
                if (cell.StyleIndex is null)
                {
                    cell.Remove();
                }

                cleared++;
            }
        }

        if (formulasRemoved && workbook.CalculationChainPart is { } chain)
        {
            workbook.DeletePart(chain);
        }

        return cleared;
    }

    /// <summary>
    /// Changes a workbook in memory and replaces the file in one step (written beside it, then moved), so a failure
    /// never leaves a half-written workbook. A new workbook is created when <paramref name="exists"/> is false.
    /// </summary>
    public static T Rewrite<T>(string full, bool exists, SpreadsheetOptions options, Func<WorkbookPart, T> change)
    {
        using var memory = new MemoryStream();
        if (exists)
        {
            using var file = File.OpenRead(full);
            file.CopyTo(memory);
        }

        T result;
        using (var document = exists ? SpreadsheetDocument.Open(memory, true, Settings(options)) : SpreadsheetDocument.Create(memory, SpreadsheetDocumentType.Workbook))
        {
            result = change(document.WorkbookPart ?? document.AddWorkbookPart());
        }

        var temporary = Path.Combine(Path.GetDirectoryName(full)!, "." + Path.GetFileName(full) + "." + Path.GetRandomFileName() + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, memory.ToArray());
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }

        return result;
    }

    private static Row RowAt(SheetData data, List<Row> rows, uint index)
    {
        int low = 0, high = rows.Count - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            var at = rows[middle].RowIndex!.Value;
            if (at == index)
            {
                return rows[middle];
            }

            if (at < index)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        var row = new Row { RowIndex = index };
        if (low < rows.Count)
        {
            data.InsertBefore(row, rows[low]);
        }
        else
        {
            data.Append(row);
        }

        rows.Insert(low, row);
        return row;
    }

    /// <summary>Sets one cell (null clears it); returns whether a formula was removed.</summary>
    private static bool SetCell(WorkbookPart workbook, Row row, int column, object? value, ref uint? dateStyle)
    {
        var rowNumber = row.RowIndex!.Value.ToString(CultureInfo.InvariantCulture);
        var reference = CellAddress.ColumnName(column) + rowNumber;
        Cell? cell = null;
        Cell? after = null;
        var lastColumn = 0;
        foreach (var existing in row.Elements<Cell>())
        {
            var at = existing.CellReference?.Value is { } text && CellAddress.TryParse(text, out var c, out _) ? c : lastColumn + 1;
            lastColumn = at;
            existing.CellReference ??= CellAddress.ColumnName(at) + rowNumber;
            if (at == column)
            {
                cell = existing;
                break;
            }

            if (at > column)
            {
                after = existing;
                break;
            }
        }

        var hadFormula = cell?.CellFormula is not null;
        if (value is null)
        {
            cell?.Remove();
            return hadFormula;
        }

        if (cell is null)
        {
            cell = new Cell { CellReference = reference };
            if (after is not null)
            {
                row.InsertBefore(cell, after);
            }
            else
            {
                row.Append(cell);
            }
        }

        cell.CellFormula?.Remove();
        cell.CellValue?.Remove();
        cell.InlineString?.Remove();
        switch (value)
        {
            case bool flag:
                cell.DataType = CellValues.Boolean;
                cell.Append(new CellValue(flag ? "1" : "0"));
                break;
            case long number:
                cell.DataType = null;
                cell.Append(new CellValue(number.ToString(CultureInfo.InvariantCulture)));
                break;
            case decimal number:
                cell.DataType = null;
                cell.Append(new CellValue(number.ToString(CultureInfo.InvariantCulture)));
                break;
            case DateTimeOffset date:
                dateStyle ??= DateStyle(workbook);
                cell.DataType = null;
                cell.StyleIndex = dateStyle.Value;
                cell.Append(new CellValue(date.UtcDateTime.ToOADate().ToString("R", CultureInfo.InvariantCulture)));
                break;
            default:
                // Text is always an inline string, never a formula: "=1+1" stays the text "=1+1".
                var text = value as string ?? WorkflowValues.ToDisplayString(value);
                if (text.Length > 32_767)
                {
                    throw new ActivityFailedException(SpreadsheetErrorTypes.InvalidInput, $"A value for cell {reference} is longer than Excel's 32767 characters.");
                }

                cell.DataType = CellValues.InlineString;
                cell.Append(new InlineString(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
                break;
        }

        return hadFormula;
    }

    /// <summary>The index of a cell format showing date and time (built-in format 22), added when missing.</summary>
    private static uint DateStyle(WorkbookPart workbook)
    {
        var part = workbook.WorkbookStylesPart ?? workbook.AddNewPart<WorkbookStylesPart>();
        part.Stylesheet ??= new Stylesheet(
            new Fonts(new Font()) { Count = 1 },
            new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2 },
            new Borders(new Border()) { Count = 1 },
            new CellFormats(new CellFormat()) { Count = 1 });
        var formats = part.Stylesheet.CellFormats ?? part.Stylesheet.AppendChild(new CellFormats(new CellFormat()));
        var list = formats.Elements<CellFormat>().ToList();
        var existing = list.FindIndex(f => f.NumberFormatId?.Value == 22 && f.ApplyNumberFormat?.Value == true);
        if (existing >= 0)
        {
            return (uint)existing;
        }

        formats.Append(new CellFormat { NumberFormatId = 22, FontId = 0, FillId = 0, BorderId = 0, FormatId = 0, ApplyNumberFormat = true });
        formats.Count = (uint)(list.Count + 1);
        return (uint)list.Count;
    }
}
