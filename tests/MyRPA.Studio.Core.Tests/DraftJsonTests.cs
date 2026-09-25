using MyRPA.Studio.Documents;
using MyRPA.Workflow.Serialization;
using MyRPA.Workflow.Validation;

namespace MyRPA.Studio.Tests;

/// <summary>Open/save fidelity: Studio's draft format is the engine's workflow JSON format.</summary>
public sealed class DraftJsonTests
{
    public static TheoryData<string> ShippedWorkflows() =>
        [.. Directory.EnumerateFiles(RepositoryPaths.Samples, "*.json", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.EndsWith("myrpa-plugin.json", StringComparison.Ordinal))
            .Select(p => Path.GetRelativePath(RepositoryPaths.Samples, p))];

    [Theory]
    [MemberData(nameof(ShippedWorkflows))]
    public void OpenThenSave_IsSemanticallyIdentical(string sample)
    {
        var original = File.ReadAllText(Path.Combine(RepositoryPaths.Samples, sample));
        var loader = new WorkflowLoader(Catalogs.BuiltIn);

        var saved = DraftJson.Write(DraftJson.Read(original));

        var before = loader.Load(original);
        var after = loader.Load(saved);
        Assert.Equal(before.Diagnostics.Select(d => d.ToString()), after.Diagnostics.Select(d => d.ToString()));
        if (before.IsValid)
        {
            Assert.Equal(WorkflowJsonWriter.Write(before.Workflow!), WorkflowJsonWriter.Write(after.Workflow!));
        }
    }

    [Fact]
    public void SaveOpenSave_IsStableText()
    {
        var once = DraftJson.Write(Drafts.Sample());

        Assert.Equal(once, DraftJson.Write(DraftJson.Read(once)));
    }

    [Fact]
    public void LiteralsMapsUnknownFieldsAndComments_ArePreserved()
    {
        var draft = DraftJson.Read("""
            // comments and trailing commas are accepted, as in the CLI
            { "schemaVersion": "1.0", "id": "w", "name": "W", "version": "1", "x-owner": { "team": "rpa" },
              "root": { "id": "r", "type": "Core.Sequence", "x-note": [1, 2], "children": [
                { "id": "d", "type": "Core.Delay", "properties": { "milliseconds": 500 } },
                { "id": "i", "type": "Core.InvokeWorkflow", "properties": { "workflow": "a.json", "arguments": { "n": 5, "s": "'x'" }, "outputs": { "o": "v" } } },
              ] } }
            """);

        var json = DraftJson.Write(draft);

        Assert.Contains("\"milliseconds\": 500", json, StringComparison.Ordinal);
        Assert.Contains("\"n\": 5", json, StringComparison.Ordinal);
        Assert.Contains("\"s\": \"'x'\"", json, StringComparison.Ordinal);
        Assert.Contains("\"x-owner\"", json, StringComparison.Ordinal);
        Assert.Contains("\"x-note\"", json, StringComparison.Ordinal);
        Assert.Equal(new ScalarValue("500", IsJsonLiteral: true), draft.Root.Children[0].Property("milliseconds"));
    }

    [Fact]
    public void SemanticallyInvalidWorkflows_StillOpen()
    {
        var draft = DraftJson.Read(File.ReadAllText(Path.Combine(RepositoryPaths.Samples, "invalid.json")));

        Assert.Equal("broken example", draft.Id);
        Assert.Contains(draft.Root.DescendantsAndSelf(), n => n.Type == "Core.Browse");
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{ "schemaVersion": "1.0", "id": "w" }""")]
    [InlineData("""{ "id": 5, "root": { "id": "r", "type": "Core.Sequence" } }""")]
    [InlineData("""{ "root": { "id": "r", "type": "Core.Sequence", "children": {} } }""")]
    [InlineData("""{ "root": { "id": "r", "type": "Core.Log", "properties": { "message": [1] } } }""")]
    [InlineData("""{ "arguments": [ { "name": "a", "direction": "In", "type": "String", "required": "yes" } ], "root": { "id": "r", "type": "Core.Sequence" } }""")]
    public void UnrepresentableFiles_AreRefusedWithAPath(string json)
    {
        var error = Assert.Throws<DraftReadException>(() => DraftJson.Read(json));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void Write_ReportsTheJsonPathOfEveryNode()
    {
        DraftJson.Write(Drafts.Sample(), out var paths);

        Assert.Equal(NodePath.Root, paths["$.root"]);
        Assert.Equal(NodePath.Root.Child(1), paths["$.root.children[1]"]);
        Assert.Equal(NodePath.Root.Child(1).Slot("then"), paths["$.root.children[1].slots.then"]);
        Assert.Equal(4, paths.Count);
    }
}
