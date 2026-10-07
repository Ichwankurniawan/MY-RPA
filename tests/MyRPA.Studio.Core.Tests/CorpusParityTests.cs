using System.Text.Json;
using System.Text.Json.Nodes;
using MyRPA.Studio.Documents;
using MyRPA.Studio.Editing;

namespace MyRPA.Studio.Tests;

/// <summary>
/// W9 parity evidence (ADR-0035): the WPF Studio's <see cref="DraftValidator"/> locates every diagnostic of the shared
/// corpus (<c>tests/corpus</c>); the locations are kept in <c>tests/corpus/expected/locations.json</c>, which the Web
/// Studio's test (<c>web/studio/src/corpus.test.ts</c>) checks its own locations against. Set MYRPA_UPDATE_CORPUS=1 to
/// rewrite the file after changing the corpus. This test adds no behavior to the frozen WPF Studio; it reads it.
/// </summary>
public sealed class CorpusParityTests
{
    private static readonly string _corpus = Path.Combine(RepositoryPaths.Root, "tests", "corpus");
    private static readonly string _golden = Path.Combine(_corpus, "expected", "locations.json");

    /// <summary>
    /// Every loader diagnostic code the WPF Studio can report. Not reachable through it: MYRPA1001/1002 (not JSON, not an
    /// object) and MYRPA1004 (wrong field type): WPF refuses to open such files (<see cref="DraftReadException"/>, recorded
    /// as unreadable); MYRPA1003 (missing field): its draft model writes a missing field as an empty one (MYRPA1021/1030).
    /// </summary>
    private static readonly string[] _requiredCodes =
    [
        "MYRPA1005", "MYRPA1010", "MYRPA1011", "MYRPA1020", "MYRPA1021", "MYRPA1030", "MYRPA1031",
        "MYRPA1032", "MYRPA1033", "MYRPA1040", "MYRPA1041", "MYRPA1042", "MYRPA1043", "MYRPA1044", "MYRPA1045", "MYRPA1046",
        "MYRPA1047", "MYRPA1050", "MYRPA1051", "MYRPA1052", "MYRPA1060", "MYRPA1061", "MYRPA1062", "MYRPA1063", "MYRPA1064",
        "MYRPA1065",
    ];

    [Fact]
    public void CorpusDiagnosticLocations_MatchTheGoldenFile_AndCoverEveryCode()
    {
        var validator = new DraftValidator(Catalogs.BuiltIn);
        var files = new JsonArray();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(_corpus, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var entry = new JsonObject { ["file"] = Path.GetFileName(file) };
            WorkflowDraft draft;
            try
            {
                draft = DraftJson.Read(File.ReadAllText(file));
            }
            catch (DraftReadException ex)
            {
                entry["readable"] = false;
                entry["reason"] = ex.Message;
                files.Add(entry);
                continue;
            }

            var diagnostics = new JsonArray();
            foreach (var located in validator.Validate(draft).Diagnostics)
            {
                var d = located.Diagnostic;
                codes.Add(d.Code);
                diagnostics.Add(new JsonObject
                {
                    ["code"] = d.Code,
                    ["severity"] = d.Severity.ToString(),
                    ["message"] = d.Message,
                    ["path"] = d.Path,
                    ["nodeId"] = d.NodeId,
                    ["wpf"] = Location(draft, located),
                });
            }

            entry["readable"] = true;
            entry["diagnostics"] = diagnostics;
            files.Add(entry);
        }

        var json = files.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Environment.GetEnvironmentVariable("MYRPA_UPDATE_CORPUS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_golden)!);
            File.WriteAllText(_golden, json);
        }

        Assert.True(File.Exists(_golden), "Run once with MYRPA_UPDATE_CORPUS=1 to write tests/corpus/expected/locations.json.");
        Assert.Equal(File.ReadAllText(_golden).ReplaceLineEndings("\n"), json.ReplaceLineEndings("\n"));
        Assert.Empty(_requiredCodes.Except(codes));
    }

    private static JsonObject Location(WorkflowDraft draft, DraftDiagnostic located) => located switch
    {
        { ArgumentIndex: { } index } => new JsonObject { ["kind"] = "row", ["list"] = "arguments", ["index"] = index },
        { VariableIndex: { } index } => new JsonObject { ["kind"] = "row", ["list"] = "variables", ["index"] = index },
        { Node: { } node } => new JsonObject
        {
            ["kind"] = "node",
            ["nodePath"] = "$.root" + string.Concat(node.Steps.Select(s => s switch
            {
                ChildStep c => $".children[{c.Index}]",
                SlotStep slot => $".slots.{slot.Name}",
                _ => throw new InvalidOperationException(),
            })),
            ["nodeId"] = DraftTree.Find(draft, node)?.Id,
            ["property"] = located.Property,
        },
        _ => new JsonObject { ["kind"] = "workflow" },
    };
}
