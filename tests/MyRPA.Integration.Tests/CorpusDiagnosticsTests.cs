using System.Text.Json;

namespace MyRPA.Integration.Tests;

/// <summary>
/// The shared diagnostics corpus (ADR-0035). Entries recorded by the archived WPF Studio are a frozen reference for
/// where diagnostics are shown (the Web Studio's <c>corpus.test.ts</c>); entries with a <c>source</c> were written from
/// the loader for format 1.1 (ADR-0037), and the real CLI must report exactly those diagnostics (code, severity, path,
/// message).
/// </summary>
public sealed class CorpusDiagnosticsTests
{
    private static string Corpus => Path.Combine(RepositoryPaths.Root, "tests", "corpus");

    public static TheoryData<string> Files()
    {
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(Corpus, "expected", "locations.json")));
        return [.. golden.RootElement.EnumerateArray().Where(e => e.TryGetProperty("source", out _)).Select(e => e.GetProperty("file").GetString()!)];
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task CorpusFile_ReportsTheRecordedDiagnostics(string file)
    {
        using var golden = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Corpus, "expected", "locations.json"), TestContext.Current.CancellationToken));
        var entry = golden.RootElement.EnumerateArray().Single(e => e.GetProperty("file").GetString() == file);
        var expected = entry.TryGetProperty("diagnostics", out var diagnostics)
            ? diagnostics.EnumerateArray().Select(d =>
                $"{d.GetProperty("severity").GetString()!.ToLowerInvariant()} {d.GetProperty("code").GetString()} {d.GetProperty("path").GetString()}: {d.GetProperty("message").GetString()}").ToList()
            : [];

        var result = await Cli.RunAsync("validate", Path.Combine(Corpus, file));
        var reported = result.Out.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("error ", StringComparison.Ordinal) || line.StartsWith("warning ", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(expected, reported);
    }

    [Fact]
    public void Corpus_CoversEveryGraphDiagnostic()
    {
        var text = File.ReadAllText(Path.Combine(Corpus, "expected", "locations.json"));

        // MYRPA1057 (a position that is not a finite number) cannot be written as JSON the Studio keeps unchanged; it is
        // covered by GraphWorkflowTests.
        Assert.All(["MYRPA1053", "MYRPA1054", "MYRPA1055", "MYRPA1056", "MYRPA1058"], code => Assert.Contains($"\"{code}\"", text, StringComparison.Ordinal));
    }
}
