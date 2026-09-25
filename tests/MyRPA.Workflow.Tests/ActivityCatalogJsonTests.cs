using MyRPA.Workflow.Serialization;
using MyRPA.Workflow.Validation;

namespace MyRPA.Workflow.Tests;

public sealed class ActivityCatalogJsonTests
{
    private static readonly TestCatalog _catalog = new();

    [Fact]
    public void WriteReadWrite_IsStable()
    {
        var json = ActivityCatalogJson.Write(_catalog.Descriptors);

        var snapshot = ActivityCatalogJson.Read(json);

        Assert.Equal(json, ActivityCatalogJson.Write(snapshot.Descriptors));
        Assert.Equal(_catalog.Descriptors.Count, snapshot.Descriptors.Count);
    }

    [Fact]
    public void Snapshot_PreservesEveryDescriptorField()
    {
        var original = _catalog.Descriptors.Single(d => d.TypeName.Value == "Core.ForEach");

        var copy = ActivityCatalogJson.Read(ActivityCatalogJson.Write(_catalog.Descriptors)).Descriptors.Single(d => d.TypeName == original.TypeName);

        Assert.Equal(original.DisplayName, copy.DisplayName);
        Assert.Equal(original.Category, copy.Category);
        Assert.Equal(original.AllowsChildren, copy.AllowsChildren);
        Assert.Equal(original.Properties.Select(p => (p.Name, p.Kind, p.IsRequired, string.Join(",", p.ScopeSlots))),
            copy.Properties.Select(p => (p.Name, p.Kind, p.IsRequired, string.Join(",", p.ScopeSlots))));
        Assert.Equal(original.Slots.Select(s => (s.Name, s.IsRequired, s.IsPrefix)), copy.Slots.Select(s => (s.Name, s.IsRequired, s.IsPrefix)));
    }

    [Fact]
    public void ValidatingAgainstASnapshot_GivesTheSameDiagnostics()
    {
        var snapshot = ActivityCatalogJson.Read(ActivityCatalogJson.Write(_catalog.Descriptors));
        const string workflow = """
            { "schemaVersion": "1.0", "id": "w", "name": "W", "version": "1",
              "variables": [ { "name": "x", "type": "Int", "default": 1 } ],
              "root": { "id": "r", "type": "Core.Sequence", "children": [
                { "id": "a", "type": "Core.Assign", "properties": { "to": "missing", "value": "x +" } },
                { "id": "l", "type": "Core.Log", "properties": { "message": "'m'", "level": "Loud" } },
                { "id": "u", "type": "Plugin.Unknown" } ] } }
            """;

        var live = new WorkflowLoader(_catalog).Load(workflow);
        var fromSnapshot = new WorkflowLoader(snapshot).Load(workflow);

        Assert.Equal(live.Diagnostics.Select(d => d.ToString()), fromSnapshot.Diagnostics.Select(d => d.ToString()));
        Assert.NotEmpty(live.Errors);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "catalogVersion": "2.0", "activities": [] }""")]
    [InlineData("""{ "catalogVersion": "1.0" }""")]
    [InlineData("""{ "catalogVersion": "1.0", "activities": [ { "type": "NoNamespace", "displayName": "d", "category": "c", "allowsChildren": false, "properties": [], "slots": [] } ] }""")]
    [InlineData("""{ "catalogVersion": "1.0", "activities": [ { "type": "A.B", "displayName": "d", "category": "c", "allowsChildren": false, "properties": [ { "name": "p", "kind": "Bogus", "required": true } ], "slots": [] } ] }""")]
    [InlineData("""{ "catalogVersion": "1.0", "activities": [ { "type": "A.B", "displayName": "d", "category": "c", "allowsChildren": false, "properties": [ { "name": "p", "kind": "1", "required": true } ], "slots": [] } ] }""")]
    [InlineData("""{ "catalogVersion": "1.0", "activities": [ { "type": "A.B", "displayName": "d", "category": "c", "allowsChildren": false, "properties": [], "slots": [] }, { "type": "A.B", "displayName": "d", "category": "c", "allowsChildren": false, "properties": [], "slots": [] } ] }""")]
    public void InvalidSnapshots_AreRejectedWithAFormatException(string json) =>
        Assert.ThrowsAny<FormatException>(() => ActivityCatalogJson.Read(json));
}
