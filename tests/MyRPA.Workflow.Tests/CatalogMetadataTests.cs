using System.Diagnostics.CodeAnalysis;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Serialization;
using MyRPA.Workflow.Validation;

namespace MyRPA.Workflow.Tests;

/// <summary>Catalog 1.2 metadata and secret properties (ADR-0042).</summary>
public sealed class CatalogMetadataTests
{
    private static readonly ActivityDescriptor _login = new(
        new ActivityTypeName("Test.Login"),
        "Login",
        "Test",
        "Signs in.",
        [
            new ActivityPropertyDefinition("user", ActivityPropertyKind.Expression, isRequired: true) { ValueType = ActivityValueType.String },
            new ActivityPropertyDefinition("token", ActivityPropertyKind.Expression, isRequired: true) { ValueType = ActivityValueType.String, IsSecret = true },
            new ActivityPropertyDefinition("timeoutMs", ActivityPropertyKind.Expression) { ValueType = ActivityValueType.Int, DefaultValue = "30000" },
        ])
    { SideEffects = ActivitySideEffects.Network | ActivitySideEffects.FileSystem };

    private static readonly WorkflowLoader _loader = new(new OneActivity(_login));

    private static string Workflow(string token) =>
        $$"""{ "schemaVersion": "1.0", "id": "w", "name": "W", "version": "1", "arguments": [ { "name": "apiToken", "direction": "In", "type": "String" } ], "root": { "id": "login", "type": "Test.Login", "properties": { "user": "'ada'", "token": {{token}} } } }""";

    [Fact]
    public void Metadata_IsChecked_WhenItIsSet()
    {
        Assert.Throws<ArgumentException>(() => new ActivityPropertyDefinition("p", ActivityPropertyKind.Expression) { DefaultValue = "not json" });
        Assert.Throws<ArgumentException>(() => new ActivityPropertyDefinition("p", ActivityPropertyKind.Text) { IsSecret = true });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivityPropertyDefinition("p", ActivityPropertyKind.Expression) { ValueType = (ActivityValueType)99 });
        Assert.Throws<ArgumentOutOfRangeException>(() => _login with { SideEffects = (ActivitySideEffects)8 });
    }

    [Fact]
    public void Catalog12_RoundTripsTheMetadata_AndOlderSnapshotsStillRead()
    {
        var json = ActivityCatalogJson.Write([_login]);
        var read = Assert.Single(ActivityCatalogJson.Read(json).Descriptors);

        Assert.Contains("\"catalogVersion\": \"1.2\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sideEffects\": [", json, StringComparison.Ordinal);
        Assert.Equal(ActivitySideEffects.Network | ActivitySideEffects.FileSystem, read.SideEffects);
        Assert.Equal(
            [(ActivityValueType.String, null, false), (ActivityValueType.String, null, true), (ActivityValueType.Int, "30000", false)],
            read.Properties.Select(p => (p.ValueType, p.DefaultValue, p.IsSecret)));

        var older = Assert.Single(ActivityCatalogJson.Read("""
            { "catalogVersion": "1.1", "activities": [ { "type": "A.B", "displayName": "d", "category": "c", "allowsChildren": false, "properties": [ { "name": "x", "kind": "Expression", "required": false } ], "slots": [] } ] }
            """).Descriptors);
        Assert.Equal((ActivitySideEffects.None, ActivityValueType.Any, false), (older.SideEffects, older.Properties[0].ValueType, older.Properties[0].IsSecret));
    }

    [Fact]
    public void Multiline_RoundTrips_AndIsOnlyForTextProperties()
    {
        var script = new ActivityDescriptor(
            new ActivityTypeName("Test.Script"),
            "Script",
            "Test",
            "Runs code.",
            [new ActivityPropertyDefinition("code", ActivityPropertyKind.Text, isRequired: true) { IsMultiline = true }, new ActivityPropertyDefinition("name", ActivityPropertyKind.Text)]);

        var json = ActivityCatalogJson.Write([script]);
        var read = Assert.Single(ActivityCatalogJson.Read(json).Descriptors);

        Assert.Equal(1, json.Split("\"multiline\": true").Length - 1);
        Assert.Equal([true, false], read.Properties.Select(p => p.IsMultiline));
        Assert.Throws<ArgumentException>(() => new ActivityPropertyDefinition("p", ActivityPropertyKind.Expression) { IsMultiline = true });
    }

    [Theory]
    [InlineData("\"valueType\": \"String\"", "\"valueType\": \"Number\"", "valueType")]
    [InlineData("\"secret\": true", "\"secret\": \"yes\"", "secret")]
    [InlineData("\"sideEffects\": [", "\"sideEffects\": [ \"Disk\", ", "sideEffects")]
    public void Catalog12_RefusesUnknownMetadata(string written, string replacement, string field)
    {
        var json = ActivityCatalogJson.Write([_login]);
        Assert.Contains(written, json, StringComparison.Ordinal);
        var broken = json.Replace(written, replacement, StringComparison.Ordinal);

        var error = Assert.Throws<FormatException>(() => ActivityCatalogJson.Read(broken));
        Assert.Contains(field, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"apiToken\"", true)]
    [InlineData("\"'Bearer ' + apiToken\"", true)]
    [InlineData("\"'s3cr3t'\"", false)]
    [InlineData("\"'s3' + 'cr3t'\"", false)]
    [InlineData("12345", false)]
    [InlineData("null", false)]
    public void SecretProperty_MustComeFromAName(string token, bool valid)
    {
        var result = _loader.Load(Workflow(token));

        Assert.Equal(valid, result.IsValid);
        if (!valid)
        {
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal((DiagnosticCodes.SecretLiteral, "$.root.properties.token"), (diagnostic.Code, diagnostic.Path));
            Assert.DoesNotContain("s3cr3t", diagnostic.Message, StringComparison.Ordinal);
        }
    }

    private sealed class OneActivity(ActivityDescriptor descriptor) : IActivityCatalog
    {
        public IReadOnlyList<ActivityDescriptor> Descriptors { get; } = [descriptor];

        public bool TryGet(ActivityTypeName typeName, [NotNullWhen(true)] out ActivityDescriptor? found)
        {
            found = typeName == descriptor.TypeName ? descriptor : null;
            return found is not null;
        }
    }
}
