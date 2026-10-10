using System.Globalization;
using MyRPA.Sdk.Files;
using MyRPA.Sdk.Plugins;

namespace MyRPA.Spreadsheet;

/// <summary>
/// Entry point of the Excel plugin (Phase 7, ADR-0042). Settings:
/// <list type="bullet">
/// <item><c>fileRoot</c> — the only folder tree the activities may use (default: the host's working directory).</item>
/// <item><c>maxFileBytes</c> — the largest workbook opened (default 50 MB).</item>
/// <item><c>maxRows</c> — the most rows read or written (default 100000).</item>
/// <item><c>maxCells</c> — the most non-empty cells read (default 2000000).</item>
/// </list>
/// </summary>
public sealed class SpreadsheetPlugin : IPlugin
{
    private SpreadsheetOptions? _options;

    /// <inheritdoc />
    public void Initialize(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings;
        var root = settings.TryGetValue("fileRoot", out var rootText) ? rootText : Environment.CurrentDirectory;
        _options = new SpreadsheetOptions(
            new FileRootPolicy(root),
            Positive(settings, "maxFileBytes", 50L * 1024 * 1024),
            (int)Positive(settings, "maxRows", 100_000),
            (int)Positive(settings, "maxCells", 2_000_000));
    }

    /// <inheritdoc />
    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        registrar
            .AddInstance(_options ?? throw new InvalidOperationException("Initialize must run before Register."))
            .AddActivity<ExcelGetSheetsActivity>(ExcelGetSheetsActivity.Descriptor)
            .AddActivity<ExcelReadRangeActivity>(ExcelReadRangeActivity.Descriptor)
            .AddActivity<ExcelWriteRangeActivity>(ExcelWriteRangeActivity.Descriptor)
            .AddActivity<ExcelAppendRowsActivity>(ExcelAppendRowsActivity.Descriptor)
            .AddActivity<ExcelClearRangeActivity>(ExcelClearRangeActivity.Descriptor);
    }

    private static long Positive(IReadOnlyDictionary<string, string> settings, string name, long defaultValue)
    {
        if (!settings.TryGetValue(name, out var text))
        {
            return defaultValue;
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is > 0 and <= int.MaxValue
            ? value
            : throw new ArgumentException($"Setting '{name}' must be a positive whole number, not '{text}'.", nameof(settings));
    }
}

/// <summary>Plugin settings, built once in <see cref="SpreadsheetPlugin.Initialize"/>.</summary>
/// <param name="Files">Where workbooks may be read and written.</param>
/// <param name="MaxFileBytes">The largest workbook opened.</param>
/// <param name="MaxRows">The most rows read or written.</param>
/// <param name="MaxCells">The most non-empty cells read.</param>
public sealed record SpreadsheetOptions(FileRootPolicy Files, long MaxFileBytes, int MaxRows, int MaxCells);

/// <summary>The errorType values of the Excel activities besides the file ones (<see cref="FileErrorTypes"/>).</summary>
public static class SpreadsheetErrorTypes
{
    /// <summary>The file is not an .xlsx workbook the activities can read (or it is damaged).</summary>
    public const string InvalidWorkbook = "InvalidWorkbook";

    /// <summary>The workbook has no sheet with that name (or it is a chart sheet).</summary>
    public const string SheetNotFound = "SheetNotFound";

    /// <summary>A property has a value of the wrong kind (a bad range or cell address included).</summary>
    public const string InvalidInput = "InvalidInput";
}
