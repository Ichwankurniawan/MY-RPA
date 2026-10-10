using static MyRPA.Documents.Tests.DocumentsHost;

namespace MyRPA.Documents.Tests;

/// <summary>Pdf.ReadText and Pdf.GetInfo on hand-written PDFs: text, page ranges, encryption, limits, damage (ADR-0043).</summary>
public sealed class PdfActivityTests(DocumentsHost host) : IClassFixture<DocumentsHost>
{
    private const string Password = "s3cret-pdf-pass";

    private static readonly string[] _invoice =
    [
        "ACME Supplies Ltd\nInvoice INV-2026-0042\nDate: 2026-10-01",
        "Line items\nPaper (A4) x 10",
        "Total: 1,234.50 EUR",
    ];

    private static int _files;

    private static IReadOnlyDictionary<string, object?> Map(object? value) => (IReadOnlyDictionary<string, object?>)value!;

    private static IReadOnlyList<object?> Items(object? value) => (IReadOnlyList<object?>)value!;

    /// <summary>Writes a PDF under the root and returns its relative path.</summary>
    private string Pdf(byte[] bytes)
    {
        var name = $"doc-{Interlocked.Increment(ref _files)}.pdf";
        File.WriteAllBytes(host.InRoot(name), bytes);
        return name;
    }

    private static Dictionary<string, object?> Secret(string value) => new() { ["pwd"] = value };

    [Fact]
    public async Task ReadText_AllPages_InOrder_SeparatedByABlankLine()
    {
        var path = Pdf(PdfFiles.Build(_invoice));

        var result = await host.RunAsync(Node("Pdf.ReadText", $$""" "path": "'{{path}}'", "result": "text", "pageTexts": "pages" """), ["text", "pages"]);

        AssertSucceeded(result);
        var text = (string)result.Outputs["text"]!;
        Assert.Contains("Invoice INV-2026-0042", text, StringComparison.Ordinal);
        Assert.Contains("Paper (A4) x 10", text, StringComparison.Ordinal);
        Assert.Contains("Total: 1,234.50 EUR", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("INV-2026-0042", StringComparison.Ordinal) < text.IndexOf("Total:", StringComparison.Ordinal));
        Assert.Equal(3, Items(result.Outputs["pages"]).Count);
        Assert.Contains("\n\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadText_ThenMatchText_ExtractsTheInvoiceNumber()
    {
        var path = Pdf(PdfFiles.Build(_invoice));

        var result = await host.RunAsync(
            Node("Pdf.ReadText", $$""" "path": "'{{path}}'", "pages": "'1'", "result": "text" """) + "," +
            Node("Core.Text.Match", """ "text": "text", "pattern": "r'INV-\\d{4}-\\d+'", "result": "match" """),
            ["text", "match"]);

        AssertSucceeded(result);
        Assert.Equal("INV-2026-0042", Map(result.Outputs["match"])["value"]);
    }

    [Theory]
    [InlineData("2", new[] { "Paper" })]
    [InlineData("2-", new[] { "Paper", "Total" })]
    [InlineData("3, 1", new[] { "Total", "ACME" })]
    [InlineData("1-2, 2", new[] { "ACME", "Paper" })]
    public async Task ReadText_APageSelection_ReadsThosePagesInThatOrder(string pages, string[] contains)
    {
        var path = Pdf(PdfFiles.Build(_invoice));

        var result = await host.RunAsync(Node("Pdf.ReadText", $$""" "path": "'{{path}}'", "pages": "'{{pages}}'", "result": "text", "pageTexts": "pages" """), ["text", "pages"]);

        AssertSucceeded(result);
        var texts = Items(result.Outputs["pages"]).Cast<string>().ToList();
        Assert.Equal(contains.Length, texts.Count);
        for (var i = 0; i < contains.Length; i++)
        {
            Assert.Contains(contains[i], texts[i], StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("4")]
    [InlineData("2-9")]
    [InlineData("0")]
    [InlineData("a-b")]
    [InlineData("3-1")]
    public async Task ReadText_ABadPageSelection_FailsInvalidInput(string pages)
    {
        var path = Pdf(PdfFiles.Build(_invoice));

        var result = await host.RunAsync(Node("Pdf.ReadText", $$""" "path": "'{{path}}'", "pages": "'{{pages}}'", "result": "text" """), ["text"]);

        host.AssertFailed(result, ErrorTypes.InvalidInput);
    }

    [Fact]
    public async Task ReadText_MorePagesThanMaxPages_FailsTooManyItems_ButASmallerSelectionReads()
    {
        var path = Pdf(PdfFiles.Build([.. Enumerable.Range(1, MaxPages + 1).Select(i => $"Page {i}")]));

        var all = await host.RunAsync(Node("Pdf.ReadText", $$""" "path": "'{{path}}'", "result": "text" """), ["text"]);
        var some = await host.RunAsync(Node("Pdf.ReadText", $$""" "path": "'{{path}}'", "pages": "'2-3'", "result": "text" """), ["text"]);

        host.AssertFailed(all, ErrorTypes.TooManyItems);
        AssertSucceeded(some);
        Assert.Contains("Page 3", (string)some.Outputs["text"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetInfo_GivesPagesAndProperties()
    {
        var info = new Dictionary<string, string> { ["Title"] = "Invoice 42", ["Author"] = "ACME Billing", ["CreationDate"] = "D:20261001093000Z" };
        var path = Pdf(PdfFiles.Build(_invoice, info));

        var result = await host.RunAsync(Node("Pdf.GetInfo", $$""" "path": "'{{path}}'", "result": "info" """), ["info"]);

        AssertSucceeded(result);
        var map = Map(result.Outputs["info"]);
        Assert.Equal(3L, map["pages"]);
        Assert.Equal("Invoice 42", map["title"]);
        Assert.Equal("ACME Billing", map["author"]);
        Assert.Null(map["subject"]);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero), map["created"]);
        Assert.Equal(false, map["encrypted"]);
        Assert.Equal("1.4", map["version"]);
    }

    [Fact]
    public async Task AnEncryptedPdf_NeedsItsPassword_WhichNeverAppearsInMessagesOrLogs()
    {
        var path = Pdf(PdfFiles.Build(_invoice, new Dictionary<string, string> { ["Title"] = "Secret invoice" }, Password));

        var without = await host.RunAsync(Node("Pdf.ReadText", $$""" "path": "'{{path}}'", "result": "text" """), ["text"]);
        var wrong = await host.RunAsync(Node("Pdf.ReadText", $$""" "path": "'{{path}}'", "password": "pwd", "result": "text" """), ["text"], Secret("not-the-password"));
        var right = await host.RunAsync(Node("Pdf.ReadText", $$""" "path": "'{{path}}'", "password": "pwd", "result": "text" """), ["text"], Secret(Password));
        var info = await host.RunAsync(Node("Pdf.GetInfo", $$""" "path": "'{{path}}'", "password": "pwd", "result": "info" """), ["info"], Secret(Password));

        host.AssertFailed(without, ErrorTypes.EncryptedDocument);
        host.AssertFailed(wrong, ErrorTypes.EncryptedDocument);
        Assert.DoesNotContain("not-the-password", wrong.Error!.Message, StringComparison.Ordinal);
        AssertSucceeded(right);
        Assert.Contains("Invoice INV-2026-0042", (string)right.Outputs["text"]!, StringComparison.Ordinal);
        AssertSucceeded(info);
        Assert.Equal(true, Map(info.Outputs["info"])["encrypted"]);
        Assert.Equal("Secret invoice", Map(info.Outputs["info"])["title"]);
        Assert.DoesNotContain(host.Logs.Messages, m => m.Contains(Password, StringComparison.Ordinal) || m.Contains("not-the-password", StringComparison.Ordinal));
    }

    [Fact]
    public void APasswordWrittenInTheWorkflow_IsRefusedByTheLoader()
    {
        var load = host.Load(Node("Pdf.ReadText", """ "path": "'a.pdf'", "password": "'hunter2'", "result": "text" """), ["text"], []);

        Assert.False(load.IsValid);
        Assert.Contains(load.Diagnostics, d => d.Code == "MYRPA1066");
    }

    [Theory]
    [InlineData("not a pdf at all")]
    [InlineData("%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 9 0 R >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF")]
    public async Task ADamagedFile_FailsInvalidDocument(string content)
    {
        var path = Pdf(System.Text.Encoding.Latin1.GetBytes(content));

        var text = await host.RunAsync(Node("Pdf.ReadText", $$""" "path": "'{{path}}'", "result": "text" """), ["text"]);
        var info = await host.RunAsync(Node("Pdf.GetInfo", $$""" "path": "'{{path}}'", "result": "info" """), ["info"]);

        host.AssertFailed(text, ErrorTypes.InvalidDocument);
        host.AssertFailed(info, ErrorTypes.InvalidDocument);
    }

    [Fact]
    public async Task ATruncatedPdf_FailsInvalidDocument()
    {
        var bytes = PdfFiles.Build(_invoice);
        var path = Pdf(bytes[..(bytes.Length / 3)]);

        var result = await host.RunAsync(Node("Pdf.ReadText", $$""" "path": "'{{path}}'", "result": "text" """), ["text"]);

        host.AssertFailed(result, ErrorTypes.InvalidDocument);
    }

    [Fact]
    public async Task AFileLargerThanMaxFileBytes_IsRefusedBeforeParsing()
    {
        var name = "large.pdf";
        File.WriteAllBytes(host.InRoot(name), new byte[MaxFileBytes + 1]);

        var result = await host.RunAsync(Node("Pdf.ReadText", $$""" "path": "'{{name}}'", "result": "text" """), ["text"]);

        host.AssertFailed(result, ErrorTypes.FileTooLarge);
    }

    [Theory]
    [InlineData("'../outside/x.pdf'", ErrorTypes.FileAccessDenied)]
    [InlineData("'missing.pdf'", ErrorTypes.FileNotFound)]
    public async Task APathThatCannotBeRead_Fails(string path, string errorType)
    {
        File.WriteAllBytes(Path.Combine(host.Outside, "x.pdf"), PdfFiles.Build(_invoice));

        var result = await host.RunAsync(Node("Pdf.ReadText", $$""" "path": "{{path}}", "result": "text" """), ["text"]);

        host.AssertFailed(result, errorType);
    }
}
