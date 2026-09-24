using System.Text.RegularExpressions;

namespace MyRPA.Architecture.Tests;

/// <summary>
/// The plugin documentation must state the trust model honestly (ADR-0015): an AssemblyLoadContext isolates loading and
/// is never presented as a sandbox or security boundary.
/// </summary>
public sealed class DocumentationTests
{
    private static readonly string[] _pluginDocuments =
    [
        "docs/architecture/plugin-system.md",
        "docs/architecture/automation-sdk.md",
        "docs/adr/0013-automation-sdk-and-activity-contract.md",
        "docs/adr/0014-plugin-manifest-lifecycle-and-loading.md",
        "docs/adr/0015-plugin-trust-model.md",
        "docs/adr/0016-verified-path-based-plugin-loading.md",
        "docs/adr/0017-browser-automation-provider.md",
        "docs/architecture/browser-automation.md",
        "samples/plugins/README.md",
        "plugins/MyRPA.Browser.Playwright/README.md",
    ];

    public static TheoryData<string> PluginDocuments() => [.. _pluginDocuments];

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Repository.Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// Splits Markdown into paragraphs and list/table items, each as one line without emphasis or quote markers, so a
    /// sentence wrapped across lines is judged as a whole.
    /// </summary>
    public static IReadOnlyList<string> Blocks(string markdown) =>
        [.. Regex.Split(markdown.Replace("\r", string.Empty, StringComparison.Ordinal), @"\n\s*\n|\n(?=\s*[-|])")
            .Select(b => Regex.Replace(b.Replace("**", string.Empty, StringComparison.Ordinal), @"(^|\n)\s*>\s?", " "))
            .Select(b => Regex.Replace(b, @"\s+", " ").Trim())
            .Where(b => b.Length > 0)];

    [Theory]
    [InlineData("docs/architecture/plugin-system.md")]
    [InlineData("docs/adr/0015-plugin-trust-model.md")]
    [InlineData("samples/plugins/README.md")]
    public void PluginDocs_StateThatLoadContextsAreNotASecurityBoundary(string document) =>
        Assert.Contains(Blocks(Read(document)), b => b.Contains("not a security boundary", StringComparison.OrdinalIgnoreCase));

    [Theory]
    [MemberData(nameof(PluginDocuments))]
    public void PluginDocs_NeverCallLoadContextsASandbox(string document)
    {
        // Every paragraph or item that mentions a sandbox must negate or reject it ("not a sandbox", "no in-process sandbox").
        var offending = Blocks(Read(document))
            .Where(b => b.Contains("sandbox", StringComparison.OrdinalIgnoreCase))
            .Where(b => !Regex.IsMatch(b, @"\b(not|no|never|rejected)\b", RegexOptions.IgnoreCase));

        Assert.Empty(offending);
    }

    [Fact]
    public void Blocks_JoinWrappedQuotedSentences()
    {
        // Guards the detector itself.
        var blocks = Blocks("> it is **not a security\n> boundary** and not\n> a sandbox.\n\n- item one\n- item two");

        Assert.Equal(["it is not a security boundary and not a sandbox.", "- item one", "- item two"], blocks);
    }

    [Fact]
    public void SandboxDetector_FlagsAPositiveClaim()
    {
        var offending = Blocks("Plugins run in a sandbox.\n\nIt is not a sandbox.")
            .Where(b => b.Contains("sandbox", StringComparison.OrdinalIgnoreCase))
            .Where(b => !Regex.IsMatch(b, @"\b(not|no|never|rejected)\b", RegexOptions.IgnoreCase));

        Assert.Equal(["Plugins run in a sandbox."], offending);
    }

    [Fact]
    public void Adrs_AreIndexed()
    {
        var index = Read("docs/adr/README.md");
        var adrs = Directory.EnumerateFiles(Path.Combine(Repository.Root, "docs", "adr"), "0*.md").Select(Path.GetFileName);

        Assert.All(adrs, adr => Assert.Contains($"({adr})", index, StringComparison.Ordinal));
    }
}
