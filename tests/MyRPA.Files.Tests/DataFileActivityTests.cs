using static MyRPA.Files.Tests.FilesHost;

namespace MyRPA.Files.Tests;

public sealed class DataFileActivityTests(FilesHost host) : IClassFixture<FilesHost>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static IReadOnlyDictionary<string, object?> Map(object? value) => (IReadOnlyDictionary<string, object?>)value!;

    private static IReadOnlyList<object?> Items(object? value) => (IReadOnlyList<object?>)value!;

    [Fact]
    public async Task CsvRead_HandlesQuotesDoubledQuotesLineBreaksAndBlankLines()
    {
        await File.WriteAllTextAsync(
            host.InRoot("quotes.csv"),
            "name,note,amount\r\n\"Smith, Ann\",\"said \"\"hi\"\"\",10\r\n\r\nBob,\"two\nlines\",\r\nCé,,3",
            Token);

        var result = await host.RunAsync(Node("Csv.Read", """ "path": "'quotes.csv'", "result": "rows" """), "rows");

        AssertSucceeded(result);
        var rows = Items(result.Outputs["rows"]);
        Assert.Equal(3, rows.Count);
        Assert.Equal("Smith, Ann", Map(rows[0])["name"]);
        Assert.Equal("said \"hi\"", Map(rows[0])["note"]);
        Assert.Equal("10", Map(rows[0])["amount"]);
        Assert.Equal("two\nlines", Map(rows[1])["note"]);
        Assert.Equal(string.Empty, Map(rows[1])["amount"]);
        Assert.Equal("Cé", Map(rows[2])["name"]);
        Assert.Equal("3", Map(rows[2])["amount"]);
    }

    [Fact]
    public async Task CsvRead_WithoutHeaderAndSemicolons_GivesLists()
    {
        await File.WriteAllTextAsync(host.InRoot("plain.csv"), "a;b\n1;2\n", Token);

        var result = await host.RunAsync(Node("Csv.Read", """ "path": "'plain.csv'", "hasHeader": "false", "delimiter": "';'", "result": "rows" """), "rows");

        AssertSucceeded(result);
        var rows = Items(result.Outputs["rows"]);
        Assert.Equal(["a", "b"], Items(rows[0]));
        Assert.Equal(["1", "2"], Items(rows[1]));
    }

    [Fact]
    public async Task CsvRead_UnclosedQuote_FailsWithInvalidCsv()
    {
        await File.WriteAllTextAsync(host.InRoot("broken.csv"), "a,b\n\"open,1\n", Token);

        host.AssertFailed(await host.RunAsync(Node("Csv.Read", """ "path": "'broken.csv'", "result": "rows" """), "rows"), ErrorTypes.InvalidCsv);
    }

    [Fact]
    public async Task CsvRead_MoreThanMaxRows_FailsWithTooManyItems()
    {
        await File.WriteAllTextAsync(host.InRoot("long.csv"), "n\n" + string.Join("\n", Enumerable.Range(0, MaxRows + 1)), Token);

        host.AssertFailed(await host.RunAsync(Node("Csv.Read", """ "path": "'long.csv'", "result": "rows" """), "rows"), ErrorTypes.TooManyItems);
    }

    [Fact]
    public async Task CsvWrite_QuotesAndGuardsFormulas_AndReadsBackTheSameTable()
    {
        await File.WriteAllTextAsync(
            host.InRoot("table.json"),
            """[ { "name": "=HYPERLINK(\"x\")", "note": "a, \"b\"\nc", "n": 5 }, { "name": "@SUM(A1)", "n": -2 } ]""",
            Token);

        var result = await host.RunAsync(
            Node("Json.ReadFile", """ "path": "'table.json'", "result": "table" """) + "," +
            Node("Csv.Write", """ "path": "'out/table.csv'", "rows": "table" """) + "," +
            Node("Csv.Read", """ "path": "'out/table.csv'", "result": "back" """),
            "table", "back");

        AssertSucceeded(result);
        var csv = await File.ReadAllTextAsync(host.InRoot("out/table.csv"), Token);
        Assert.Equal("name,note,n\r\n\"'=HYPERLINK(\"\"x\"\")\",\"a, \"\"b\"\"\nc\",5\r\n'@SUM(A1),,-2\r\n", csv);
        var back = Items(result.Outputs["back"]);
        Assert.Equal("'=HYPERLINK(\"x\")", Map(back[0])["name"]);
        Assert.Equal("a, \"b\"\nc", Map(back[0])["note"]);
    }

    [Fact]
    public async Task CsvWrite_WithoutFormulaGuardAndOverwrite_WritesTextAsIs()
    {
        await File.WriteAllTextAsync(host.InRoot("plain.json"), """[ [ "=1+1", "x" ] ]""", Token);
        await File.WriteAllTextAsync(host.InRoot("exists.csv"), "old", Token);

        var refused = await host.RunAsync(
            Node("Json.ReadFile", """ "path": "'plain.json'", "result": "rows" """) + "," +
            Node("Csv.Write", """ "path": "'exists.csv'", "rows": "rows", "protectFormulas": "false" """),
            "rows");
        var replaced = await host.RunAsync(
            Node("Json.ReadFile", """ "path": "'plain.json'", "result": "rows" """) + "," +
            Node("Csv.Write", """ "path": "'exists.csv'", "rows": "rows", "protectFormulas": "false", "overwrite": "true" """),
            "rows");

        host.AssertFailed(refused, ErrorTypes.FileAlreadyExists);
        AssertSucceeded(replaced);
        Assert.Equal("=1+1,x\r\n", await File.ReadAllTextAsync(host.InRoot("exists.csv"), Token));
    }

    [Fact]
    public async Task JsonWriteFile_ThenReadFile_RoundTrips()
    {
        await File.WriteAllTextAsync(host.InRoot("in.json"), """{ "a": [1, 2.5, "x", true, null], "b": { "c": "d" } }""", Token);

        var result = await host.RunAsync(
            Node("Json.ReadFile", """ "path": "'in.json'", "result": "value" """) + "," +
            Node("Json.WriteFile", """ "path": "'json/out.json'", "value": "value", "indented": "false" """) + "," +
            Node("Json.ReadFile", """ "path": "'json/out.json'", "result": "back" """),
            "value", "back");

        AssertSucceeded(result);
        Assert.Equal("""{"a":[1,2.5,"x",true,null],"b":{"c":"d"}}""", await File.ReadAllTextAsync(host.InRoot("json/out.json"), Token));
        Assert.Equal([1L, 2.5m, "x", true, null], Items(Map(result.Outputs["back"])["a"]));
    }

    [Theory]
    [InlineData("{ \"a\": ")]
    [InlineData("not json")]
    public async Task JsonReadFile_NotJson_FailsWithInvalidJson(string text)
    {
        await File.WriteAllTextAsync(host.InRoot("bad.json"), text, Token);

        host.AssertFailed(await host.RunAsync(Node("Json.ReadFile", """ "path": "'bad.json'", "result": "x" """), "x"), ErrorTypes.InvalidJson);
    }

    [Fact]
    public async Task JsonReadFile_TooDeep_FailsWithInvalidJson()
    {
        await File.WriteAllTextAsync(host.InRoot("deep.json"), new string('[', 100) + new string(']', 100), Token);

        host.AssertFailed(await host.RunAsync(Node("Json.ReadFile", """ "path": "'deep.json'", "result": "x" """), "x"), ErrorTypes.InvalidJson);
    }

    [Fact]
    public async Task XmlReadFile_GivesElementsAttributesTextAndChildren()
    {
        await File.WriteAllTextAsync(
            host.InRoot("order.xml"),
            """<?xml version="1.0"?><order id="7"><!-- note --><item sku="A">Pen</item><item sku="B"><![CDATA[<Ink>]]></item><empty/></order>""",
            Token);

        var result = await host.RunAsync(Node("Xml.ReadFile", """ "path": "'order.xml'", "result": "order" """), "order");

        AssertSucceeded(result);
        var order = Map(result.Outputs["order"]);
        Assert.Equal("order", order["name"]);
        Assert.Equal("7", Map(order["attributes"])["id"]);
        Assert.Null(order["text"]);
        var children = Items(order["children"]);
        Assert.Equal(3, children.Count);
        Assert.Equal("Pen", Map(children[0])["text"]);
        Assert.Equal("B", Map(Map(children[1])["attributes"])["sku"]);
        Assert.Equal("<Ink>", Map(children[1])["text"]);
        Assert.Equal("empty", Map(children[2])["name"]);
    }

    [Theory]
    [InlineData("""<?xml version="1.0"?><!DOCTYPE r [<!ENTITY x SYSTEM "file:///etc/passwd">]><r>&x;</r>""")]
    [InlineData("""<?xml version="1.0"?><!DOCTYPE r [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">]><r>&b;</r>""")]
    [InlineData("""<r><unclosed></r>""")]
    public async Task XmlReadFile_DtdEntitiesOrBadXml_FailWithInvalidXml(string xml)
    {
        await File.WriteAllTextAsync(host.InRoot("hostile.xml"), xml, Token);

        host.AssertFailed(await host.RunAsync(Node("Xml.ReadFile", """ "path": "'hostile.xml'", "result": "x" """), "x"), ErrorTypes.InvalidXml);
    }

    [Fact]
    public async Task XmlReadFile_TooDeep_FailsWithInvalidXml()
    {
        await File.WriteAllTextAsync(host.InRoot("deep.xml"), string.Concat(Enumerable.Repeat("<a>", 70)) + string.Concat(Enumerable.Repeat("</a>", 70)), Token);

        host.AssertFailed(await host.RunAsync(Node("Xml.ReadFile", """ "path": "'deep.xml'", "result": "x" """), "x"), ErrorTypes.InvalidXml);
    }
}
