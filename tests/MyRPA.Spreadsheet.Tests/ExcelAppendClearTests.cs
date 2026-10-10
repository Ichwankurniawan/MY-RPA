using System.IO.Compression;
using MyRPA.Workflow.Values;
using static MyRPA.Spreadsheet.Tests.SpreadsheetHost;

namespace MyRPA.Spreadsheet.Tests;

/// <summary>Excel.AppendRows and Excel.ClearRange on generated workbooks (ADR-0043).</summary>
public sealed class ExcelAppendClearTests(SpreadsheetHost host) : IClassFixture<SpreadsheetHost>
{
    /// <summary>Row 1 a header (shared and inline strings), rows 2–3 values, row 3 a date with a style, row 4 a formula.</summary>
    private const string Register = """
        <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="inlineStr"><is><t>Amount</t></is></c><c r="C1" t="inlineStr"><is><t>Due</t></is></c></row>
        <row r="2"><c r="A2" t="inlineStr"><is><t>INV-1</t></is></c><c r="B2"><v>10</v></c></row>
        <row r="3"><c r="A3" t="inlineStr"><is><t>INV-2</t></is></c><c r="B3"><v>20</v></c><c r="C3" s="1"><v>45292</v></c></row>
        <row r="4"><c r="B4"><f>B2+B3</f><v>30</v></c></row>
        """;

    private const string Styles = """
        <fonts count="1"><font/></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
        <borders count="1"><border/></borders>
        <cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="14" applyNumberFormat="1"/></cellXfs>
        """;

    private static int _files;

    private static IReadOnlyDictionary<string, object?> Map(object? value) => (IReadOnlyDictionary<string, object?>)value!;

    private static IReadOnlyList<object?> Items(object? value) => (IReadOnlyList<object?>)value!;

    private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] cells) =>
        WorkflowValues.Dictionary(cells.Select(c => new KeyValuePair<string, object?>(c.Key, c.Value)));

    private static string Name(string stem) => $"{stem}-{Interlocked.Increment(ref _files)}.xlsx";

    /// <summary>The XML of the workbook's only worksheet part, whatever the part is named.</summary>
    private string SheetXml(string name)
    {
        using var zip = ZipFile.OpenRead(host.InRoot(name));
        var part = zip.Entries.Single(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal));
        using var reader = new StreamReader(part.Open());
        return reader.ReadToEnd();
    }

    private string Sample()
    {
        var name = Name("register");
        Xlsx.Write(host.InRoot(name), [("Invoices", Register)], """<si><t>Invoice</t></si>""", Styles);
        return name;
    }

    private async Task<IReadOnlyList<object?>> ReadAsync(string name, string sheet)
    {
        var result = await host.RunAsync(Node("Excel.ReadRange", $$""" "path": "'{{name}}'", "sheet": "'{{sheet}}'", "result": "rows" """), ["rows"]);
        AssertSucceeded(result);
        return Items(result.Outputs["rows"]);
    }

    [Fact]
    public async Task AppendRows_ToANewWorkbook_WritesTheHeaderThenTheRows()
    {
        var name = Name("new");

        var result = await host.RunAsync(
            Node("Excel.AppendRows", $$""" "path": "'{{name}}'", "sheet": "'Log'", "rows": "rows", "result": "first" """),
            ["first"],
            new Dictionary<string, object?> { ["rows"] = WorkflowValues.List([Row(("Invoice", "INV-9"), ("Amount", 99L))]) });

        AssertSucceeded(result);
        Assert.Equal(2L, result.Outputs["first"]);
        var row = Map(Assert.Single(await ReadAsync(name, "Log")));
        Assert.Equal("INV-9", row["Invoice"]);
        Assert.Equal(99L, row["Amount"]);
    }

    [Fact]
    public async Task AppendRows_ToAnExistingSheet_GoesBelowTheLastRow_UnderTheHeaderByName()
    {
        var name = Sample();

        var result = await host.RunAsync(
            Node("Excel.AppendRows", $$""" "path": "'{{name}}'", "sheet": "'Invoices'", "rows": "rows", "result": "first" """),
            ["first"],
            new Dictionary<string, object?> { ["rows"] = WorkflowValues.List([Row(("Amount", 7L), ("Invoice", "INV-3")), Row(("Invoice", "INV-4"))]) });

        AssertSucceeded(result);
        Assert.Equal(5L, result.Outputs["first"]);
        var rows = await ReadAsync(name, "Invoices");
        var appended = Map(rows[^2]);
        Assert.Equal("INV-3", appended["Invoice"]);
        Assert.Equal(7L, appended["Amount"]);
        Assert.Null(appended["Due"]);
        Assert.Equal("INV-4", Map(rows[^1])["Invoice"]);
        Assert.Equal(30L, Map(rows[2])["Amount"]);
    }

    [Fact]
    public async Task AppendRows_AColumnTheHeaderLacks_FailsAndLeavesTheFileUnchanged()
    {
        var name = Sample();
        var before = File.ReadAllBytes(host.InRoot(name));

        var result = await host.RunAsync(
            Node("Excel.AppendRows", $$""" "path": "'{{name}}'", "sheet": "'Invoices'", "rows": "rows" """),
            inputs: new Dictionary<string, object?> { ["rows"] = WorkflowValues.List([Row(("Invoice", "INV-5"), ("Customer", "Ada"))]) });

        host.AssertFailed(result, ErrorTypes.InvalidInput);
        Assert.Contains("Customer", result.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(host.InRoot(name)));
    }

    [Fact]
    public async Task AppendRows_ListsByPosition_TwiceInARow_AndTextNeverBecomesAFormula()
    {
        var name = Name("lists");
        var rows = WorkflowValues.List([WorkflowValues.List(["=1+1", 1L]), WorkflowValues.List(["b", 2L])]);

        var result = await host.RunAsync(
            Node("Excel.AppendRows", $$""" "path": "'{{name}}'", "rows": "rows", "result": "a" """) + "," +
            Node("Excel.AppendRows", $$""" "path": "'{{name}}'", "rows": "rows", "startColumn": "'B'", "result": "b" """),
            ["a", "b"],
            new Dictionary<string, object?> { ["rows"] = rows });

        AssertSucceeded(result);
        Assert.Equal(1L, result.Outputs["a"]);
        Assert.Equal(3L, result.Outputs["b"]);
        var sheet = SheetXml(name);
        Assert.DoesNotContain(":f>", sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", sheet, StringComparison.Ordinal);
        Assert.Contains("=1+1", sheet, StringComparison.Ordinal);
        Assert.Contains("r=\"B3\"", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppendRows_WithoutCreateFile_ToAMissingWorkbook_FailsFileNotFound()
    {
        var result = await host.RunAsync(
            Node("Excel.AppendRows", """ "path": "'missing.xlsx'", "rows": "rows", "createFile": false """),
            inputs: new Dictionary<string, object?> { ["rows"] = WorkflowValues.List([WorkflowValues.List([1L])]) });

        host.AssertFailed(result, ErrorTypes.FileNotFound);
        Assert.False(File.Exists(host.InRoot("missing.xlsx")));
    }

    [Fact]
    public async Task ClearRange_RemovesValuesAndFormulas_AndKeepsFormatting()
    {
        var name = Sample();

        var result = await host.RunAsync(Node("Excel.ClearRange", $$""" "path": "'{{name}}'", "sheet": "'Invoices'", "range": "'B3:C4'", "result": "cleared" """), ["cleared"]);

        AssertSucceeded(result);
        Assert.Equal(3L, result.Outputs["cleared"]);
        var rows = await ReadAsync(name, "Invoices");
        Assert.Equal(10L, Map(rows[0])["Amount"]);
        Assert.Equal("INV-2", Map(rows[1])["Invoice"]);
        Assert.Null(Map(rows[1])["Amount"]);
        Assert.Null(Map(rows[1])["Due"]);
        var sheet = SheetXml(name);
        Assert.Matches("r=\"C3\"[^>]*s=\"1\"|s=\"1\"[^>]*r=\"C3\"", sheet);
        Assert.DoesNotContain("B2+B3", sheet, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("'Nope'", "'A1:B2'", ErrorTypes.SheetNotFound)]
    [InlineData("'Invoices'", "'not a range'", ErrorTypes.InvalidInput)]
    public async Task ClearRange_ABadSheetOrRange_Fails(string sheet, string range, string errorType)
    {
        var name = Sample();

        var result = await host.RunAsync(Node("Excel.ClearRange", $$""" "path": "'{{name}}'", "sheet": "{{sheet}}", "range": "{{range}}" """));

        host.AssertFailed(result, errorType);
    }

    [Fact]
    public async Task ClearRange_AMissingWorkbook_FailsFileNotFound()
    {
        var result = await host.RunAsync(Node("Excel.ClearRange", """ "path": "'nothing.xlsx'", "range": "'A1'" """));

        host.AssertFailed(result, ErrorTypes.FileNotFound);
    }
}
