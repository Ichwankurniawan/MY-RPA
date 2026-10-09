using Microsoft.Extensions.DependencyInjection;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Values;
using static MyRPA.Spreadsheet.Tests.SpreadsheetHost;

namespace MyRPA.Spreadsheet.Tests;

public sealed class ExcelActivityTests(SpreadsheetHost host) : IClassFixture<SpreadsheetHost>
{
    private const string Strings = """<si><t>Name</t></si><si><t>Amount</t></si><si><r><t>Ri</t></r><r><t>ch</t></r></si>""";

    private const string Styles = """
        <numFmts count="1"><numFmt numFmtId="164" formatCode="yyyy\-mm\-dd&quot; at &quot;hh:mm"/></numFmts>
        <fonts count="1"><font/></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
        <borders count="1"><border/></borders>
        <cellXfs count="4"><xf numFmtId="0"/><xf numFmtId="14" applyNumberFormat="1"/><xf numFmtId="164" applyNumberFormat="1"/><xf numFmtId="4" applyNumberFormat="1"/></cellXfs>
        """;

    /// <summary>Row 1 header; row 2 and 3 values of every kind; row 4 empty; row 5 a formula with a text result.</summary>
    private const string DataRows = """
        <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="inlineStr"><is><t>When</t></is></c><c r="D1" t="inlineStr"><is><t>Ok</t></is></c><c r="E1" t="inlineStr"><is><t>Calc</t></is></c></row>
        <row r="2"><c r="A2" t="s"><v>2</v></c><c r="B2"><v>42</v></c><c r="C2" s="1"><v>45292</v></c><c r="D2" t="b"><v>1</v></c><c r="E2"><f>B2*2</f><v>84</v></c></row>
        <row r="3"><c r="A3" t="inlineStr"><is><t>Bob</t></is></c><c r="B3" s="3"><v>2.5</v></c><c r="C3" s="2"><v>45292.5</v></c><c r="D3" t="b"><v>0</v></c><c r="E3" t="e"><v>#DIV/0!</v></c></row>
        <row r="5"><c r="A5" t="str"><f>"x"&amp;"y"</f><v>xy</v></c></row>
        """;

    private static IReadOnlyDictionary<string, object?> Map(object? value) => (IReadOnlyDictionary<string, object?>)value!;

    private static IReadOnlyList<object?> Items(object? value) => (IReadOnlyList<object?>)value!;

    private string Sample(string name)
    {
        var path = host.InRoot(name);
        Xlsx.Write(path, [("Data", DataRows), ("Other", """<row r="3"><c r="C3" t="inlineStr"><is><t>x</t></is></c></row><row r="4"><c r="D4"><v>7</v></c></row>""")], Strings, Styles);
        return path;
    }

    [Fact]
    public async Task GetSheets_ListsTheSheetsInOrder()
    {
        Sample("sheets.xlsx");

        var result = await host.RunAsync(Node("Excel.GetSheets", """ "path": "'sheets.xlsx'", "result": "names" """), ["names"]);

        AssertSucceeded(result);
        Assert.Equal(["Data", "Other"], Items(result.Outputs["names"]));
    }

    [Fact]
    public async Task ReadRange_WithHeader_GivesTypedValuesCachedFormulasAndEmptyRows()
    {
        Sample("read.xlsx");

        var result = await host.RunAsync(Node("Excel.ReadRange", """ "path": "'read.xlsx'", "result": "rows" """), ["rows"]);

        AssertSucceeded(result);
        var rows = Items(result.Outputs["rows"]);
        Assert.Equal(4, rows.Count);
        var first = Map(rows[0]);
        Assert.Equal("Rich", first["Name"]);
        Assert.Equal(42L, first["Amount"]);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), first["When"]);
        Assert.Equal(true, first["Ok"]);
        Assert.Equal(84L, first["Calc"]);
        var second = Map(rows[1]);
        Assert.Equal("Bob", second["Name"]);
        Assert.Equal(2.5m, second["Amount"]);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero), second["When"]);
        Assert.Equal(false, second["Ok"]);
        Assert.Equal("#DIV/0!", second["Calc"]);
        Assert.All(Map(rows[2]).Values, Assert.Null);
        Assert.Equal("xy", Map(rows[3])["Name"]);
    }

    [Fact]
    public async Task ReadRange_WithARangeAndNoHeader_GivesLists()
    {
        Sample("range.xlsx");

        var result = await host.RunAsync(Node("Excel.ReadRange", """ "path": "'range.xlsx'", "sheet": "'data'", "range": "'B2:C3'", "hasHeader": "false", "result": "rows" """), ["rows"]);

        AssertSucceeded(result);
        var rows = Items(result.Outputs["rows"]);
        Assert.Equal(2, rows.Count);
        Assert.Equal(42L, Items(rows[0])[0]);
        Assert.Equal(2.5m, Items(rows[1])[0]);
    }

    [Fact]
    public async Task ReadRange_WithoutARange_StartsAtTheFirstUsedCell()
    {
        Sample("used.xlsx");

        var result = await host.RunAsync(Node("Excel.ReadRange", """ "path": "'used.xlsx'", "sheet": "'Other'", "hasHeader": "false", "result": "rows" """), ["rows"]);

        AssertSucceeded(result);
        var rows = Items(result.Outputs["rows"]);
        Assert.Equal(2, rows.Count);
        Assert.Equal(["x", null], Items(rows[0]));
        Assert.Equal([null, 7L], Items(rows[1]));
    }

    [Fact]
    public async Task WriteRange_CreatesAWorkbook_ThatReadsBackTheSameTable()
    {
        var table = WorkflowValues.List(
        [
            WorkflowValues.Dictionary([new("Name", "Ada"), new("Amount", 12L), new("Rate", 0.25m), new("Paid", true), new("Due", new DateTimeOffset(2024, 3, 4, 5, 6, 0, TimeSpan.Zero)), new("Formula", "=1+1")]),
            WorkflowValues.Dictionary([new("Name", "Bob"), new("Amount", null), new("Paid", false)]),
        ]);

        var result = await host.RunAsync(
            Node("Excel.WriteRange", """ "path": "'out/new.xlsx'", "sheet": "'Report'", "rows": "table" """) + "," +
            Node("Excel.ReadRange", """ "path": "'out/new.xlsx'", "sheet": "'Report'", "result": "back" """) + "," +
            Node("Excel.GetSheets", """ "path": "'out/new.xlsx'", "result": "sheets" """),
            ["back", "sheets"],
            new Dictionary<string, object?> { ["table"] = table });

        AssertSucceeded(result);
        Assert.Equal(["Report"], Items(result.Outputs["sheets"]));
        var back = Items(result.Outputs["back"]);
        Assert.Equal(2, back.Count);
        var ada = Map(back[0]);
        Assert.Equal("Ada", ada["Name"]);
        Assert.Equal(12L, ada["Amount"]);
        Assert.Equal(0.25m, ada["Rate"]);
        Assert.Equal(true, ada["Paid"]);
        Assert.Equal(new DateTimeOffset(2024, 3, 4, 5, 6, 0, TimeSpan.Zero), ada["Due"]);
        Assert.Equal("=1+1", ada["Formula"]);
        var bob = Map(back[1]);
        Assert.Null(bob["Amount"]);
        Assert.Null(bob["Rate"]);
        Assert.Equal(false, bob["Paid"]);

        var sheetXml = Xlsx.ReadPart(host.InRoot("out/new.xlsx"), "xl/worksheets/sheet1.xml");
        Assert.DoesNotContain(":f>", sheetXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<f>", sheetXml, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(host.InRoot("out"), "*.tmp"));
    }

    [Fact]
    public async Task WriteRange_IntoAnExistingWorkbook_ChangesOnlyTheWrittenCellsAndAddsSheets()
    {
        Sample("existing.xlsx");
        var rows = WorkflowValues.List([WorkflowValues.List(["new", 1L]), WorkflowValues.List([null, 2L])]);

        var result = await host.RunAsync(
            Node("Excel.WriteRange", """ "path": "'existing.xlsx'", "sheet": "'Data'", "startCell": "'D2'", "rows": "rows" """) + "," +
            Node("Excel.WriteRange", """ "path": "'existing.xlsx'", "sheet": "'Added'", "rows": "rows" """) + "," +
            Node("Excel.ReadRange", """ "path": "'existing.xlsx'", "sheet": "'Data'", "result": "data" """) + "," +
            Node("Excel.GetSheets", """ "path": "'existing.xlsx'", "result": "sheets" """),
            ["data", "sheets"],
            new Dictionary<string, object?> { ["rows"] = rows });

        AssertSucceeded(result);
        Assert.Equal(["Data", "Other", "Added"], Items(result.Outputs["sheets"]));
        var data = Items(result.Outputs["data"]);
        var first = Map(data[0]);
        Assert.Equal("Rich", first["Name"]);
        Assert.Equal(42L, first["Amount"]);
        Assert.Equal("new", first["Ok"]);
        Assert.Equal(1L, first["Calc"]);
        var second = Map(data[1]);
        Assert.Null(second["Ok"]);
        Assert.Equal(2L, second["Calc"]);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero), second["When"]);
    }

    [Fact]
    public async Task WriteRange_MissingWorkbookWithoutCreateFile_FailsWithFileNotFound()
    {
        var result = await host.RunAsync(
            Node("Excel.WriteRange", """ "path": "'missing.xlsx'", "rows": "rows", "createFile": "false" """),
            null,
            new Dictionary<string, object?> { ["rows"] = WorkflowValues.List([WorkflowValues.List(["a"])]) });

        host.AssertFailed(result, ErrorTypes.FileNotFound);
        Assert.False(File.Exists(host.InRoot("missing.xlsx")));
    }

    [Theory]
    [InlineData("Excel.ReadRange", "'../outside/x.xlsx'", ErrorTypes.FileAccessDenied)]
    [InlineData("Excel.GetSheets", "'none.xlsx'", ErrorTypes.FileNotFound)]
    [InlineData("Excel.GetSheets", "'notes.txt'", ErrorTypes.InvalidWorkbook)]
    [InlineData("Excel.GetSheets", "'broken.xlsx'", ErrorTypes.InvalidWorkbook)]
    public async Task BadPathsAndFiles_FailWithTheirErrorType(string type, string path, string errorType)
    {
        await File.WriteAllTextAsync(host.InRoot("notes.txt"), "not a workbook", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(host.InRoot("broken.xlsx"), "PK not really a zip", TestContext.Current.CancellationToken);

        host.AssertFailed(await host.RunAsync(Node(type, $$""" "path": "{{path}}", "result": "x" """), ["x"]), errorType);
    }

    [Fact]
    public async Task WriteRange_OutsideTheRoot_IsDeniedAndCreatesNothing()
    {
        var result = await host.RunAsync(
            Node("Excel.WriteRange", """ "path": "'../outside/out.xlsx'", "rows": "rows" """),
            null,
            new Dictionary<string, object?> { ["rows"] = WorkflowValues.List([WorkflowValues.List(["a"])]) });

        host.AssertFailed(result, ErrorTypes.FileAccessDenied);
        Assert.Empty(Directory.GetFileSystemEntries(host.Outside));
    }

    [Theory]
    [InlineData("\"sheet\": \"'Nope'\"", ErrorTypes.SheetNotFound)]
    [InlineData("\"range\": \"'A1:ZZZZ9'\"", ErrorTypes.InvalidInput)]
    [InlineData("\"range\": \"'1A'\"", ErrorTypes.InvalidInput)]
    public async Task ReadRange_BadSheetOrRange_Fails(string property, string errorType)
    {
        Sample("bad-args.xlsx");

        host.AssertFailed(await host.RunAsync(Node("Excel.ReadRange", $$""" "path": "'bad-args.xlsx'", {{property}}, "result": "x" """), ["x"]), errorType);
    }

    [Fact]
    public async Task ReadRange_LargeSheet_StreamsARangeAndRefusesTooManyRows()
    {
        var rows = string.Concat(Enumerable.Range(1, 20_000).Select(i => $"""<row r="{i}"><c r="A{i}"><v>{i}</v></c></row>"""));
        Xlsx.Write(host.InRoot("large.xlsx"), [("Big", rows)]);

        var range = await host.RunAsync(Node("Excel.ReadRange", """ "path": "'large.xlsx'", "range": "'A1:A50'", "hasHeader": "false", "result": "rows" """), ["rows"]);
        var all = await host.RunAsync(Node("Excel.ReadRange", """ "path": "'large.xlsx'", "hasHeader": "false", "result": "rows" """), ["rows"]);

        AssertSucceeded(range);
        Assert.Equal(50, Items(range.Outputs["rows"]).Count);
        Assert.Equal(50L, Items(Items(range.Outputs["rows"])[49])[0]);
        host.AssertFailed(all, ErrorTypes.TooManyItems);
    }

    [Fact]
    public async Task ReadRange_MoreThanMaxCells_FailsWithTooManyItems()
    {
        var rows = string.Concat(Enumerable.Range(1, 30).Select(r => $"""<row r="{r}">{string.Concat(Enumerable.Range(1, 40).Select(c => $"<c><v>{c}</v></c>"))}</row>"""));
        Xlsx.Write(host.InRoot("wide.xlsx"), [("Wide", rows)]);

        host.AssertFailed(await host.RunAsync(Node("Excel.ReadRange", """ "path": "'wide.xlsx'", "result": "rows" """), ["rows"]), ErrorTypes.TooManyItems);
    }

    [Fact]
    public async Task WriteRange_MoreThanMaxRows_IsRefused()
    {
        var rows = WorkflowValues.List(Enumerable.Range(0, MaxRows + 2).Select(i => (object?)WorkflowValues.List([(long)i])));

        var result = await host.RunAsync(Node("Excel.WriteRange", """ "path": "'too-many.xlsx'", "rows": "rows" """), null, new Dictionary<string, object?> { ["rows"] = rows });

        host.AssertFailed(result, ErrorTypes.TooManyItems);
        Assert.False(File.Exists(host.InRoot("too-many.xlsx")));
    }

    [Fact]
    public async Task ConcurrentWrites_ToDifferentWorkbooks_DoNotInterfere()
    {
        var runs = Enumerable.Range(0, 6).Select(i => host.RunAsync(
            Node("Excel.WriteRange", $$""" "path": "'parallel/{{i}}.xlsx'", "rows": "rows" """) + "," +
            Node("Excel.ReadRange", $$""" "path": "'parallel/{{i}}.xlsx'", "hasHeader": "false", "result": "back" """),
            ["back"],
            new Dictionary<string, object?> { ["rows"] = WorkflowValues.List([WorkflowValues.List([$"run {i}", (long)i])]) }));

        var results = await Task.WhenAll(runs);

        for (var i = 0; i < results.Length; i++)
        {
            AssertSucceeded(results[i]);
            Assert.Equal([$"run {i}", (long)i], Items(Items(results[i].Outputs["back"])[0]));
        }
    }

    [Fact]
    public void Catalog_DescribesTheFileSystemSideEffect()
    {
        var catalog = host.Services.GetRequiredService<IActivityCatalog>();

        foreach (var type in new[] { "Excel.GetSheets", "Excel.ReadRange", "Excel.WriteRange" })
        {
            Assert.True(catalog.TryGet(new ActivityTypeName(type), out var descriptor), type);
            Assert.Equal(ActivitySideEffects.FileSystem, descriptor.SideEffects);
        }
    }
}
