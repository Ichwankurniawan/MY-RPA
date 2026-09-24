using MyRPA.Plugins.Manifest;
using MyRPA.Workflow.Validation;

namespace MyRPA.Plugins.Tests;

public sealed class PluginManifestReaderTests
{
    private static PluginManifestReadResult Read(string json) => PluginManifestReader.Read(json, "test-dir");

    private static IEnumerable<string> ErrorCodes(PluginManifestReadResult result) =>
        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Code);

    [Fact]
    public void ValidManifest_ReadsEveryField()
    {
        var result = Read(Manifests.Fixture(
            capabilities: """["FileSystem"]""",
            dependencies: """[{ "id": "Tests.Other", "version": "1.2.0" }]"""));

        Assert.Empty(result.Diagnostics);
        var manifest = result.Manifest!;
        Assert.Equal("Tests.Fixture", manifest.Id.Value);
        Assert.Equal("1.0.0", manifest.Version.ToString());
        Assert.Equal("1.0", manifest.SdkVersion.ToString());
        Assert.Equal("net10.0", manifest.TargetFramework);
        Assert.Equal(new PluginEntryPoint("MyRPA.Tests.FixturePlugin.dll", Manifests.FixtureType), manifest.EntryPoint);
        Assert.Equal(["FileSystem"], manifest.Capabilities);
        Assert.Equal(Manifests.FixtureActivities, manifest.Activities.Select(a => a.Value));
        Assert.Equal(["Fixture.Provider"], manifest.Providers.Select(p => p.Value));
        var dependency = Assert.Single(manifest.Dependencies);
        Assert.Equal("Tests.Other", dependency.Id.Value);
        Assert.Equal("1.2.0", dependency.MinimumVersion.ToString());
    }

    [Fact]
    public void MinimalManifest_DefaultsOptionalListsToEmpty()
    {
        var result = Read("""
            { "manifestVersion": "1.0", "id": "A.B", "name": "n", "version": "0.1.0", "sdkVersion": "1.0",
              "targetFramework": "net10.0", "entryPoint": { "assembly": "a.dll", "type": "A.B" } }
            """);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.Manifest!.Activities);
        Assert.Empty(result.Manifest.Providers);
        Assert.Empty(result.Manifest.Capabilities);
        Assert.Empty(result.Manifest.Dependencies);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    public void MalformedManifest_IsReported(string json)
    {
        var result = Read(json);

        Assert.Null(result.Manifest);
        Assert.Equal([PluginDiagnosticCodes.ManifestMalformed], ErrorCodes(result));
    }

    [Fact]
    public void MissingFields_AreAllReportedTogether()
    {
        var result = Read("""{ "manifestVersion": "1.0" }""");

        Assert.Null(result.Manifest);
        var missing = result.Diagnostics.Where(d => d.Code == PluginDiagnosticCodes.MissingField).Select(d => d.Path).Order(StringComparer.Ordinal);
        Assert.Equal(["$.entryPoint", "$.id", "$.name", "$.sdkVersion", "$.targetFramework", "$.version"], missing);
    }

    [Fact]
    public void UnsupportedManifestVersion_StopsReading()
    {
        var result = Read("""{ "manifestVersion": "2.0", "id": 42 }""");

        Assert.Equal([PluginDiagnosticCodes.UnsupportedManifestVersion], ErrorCodes(result));
    }

    [Theory]
    [InlineData("id", "\"not-an-id\"", "$.id")]
    [InlineData("id", "\"Single\"", "$.id")]
    [InlineData("version", "\"1.0\"", "$.version")]
    [InlineData("version", "\"01.0.0\"", "$.version")]
    [InlineData("sdkVersion", "\"1\"", "$.sdkVersion")]
    [InlineData("targetFramework", "\"net48\"", "$.targetFramework")]
    [InlineData("targetFramework", "\"net10.0-android\"", "$.targetFramework")]
    [InlineData("entryPoint", """{ "assembly": "../evil.dll", "type": "A.B" }""", "$.entryPoint.assembly")]
    [InlineData("entryPoint", """{ "assembly": "a.dll", "type": "System.Collections.Generic.List`1" }""", "$.entryPoint.type")]
    [InlineData("entryPoint", """{ "assembly": "a.dll", "type": "A.B, OtherAssembly" }""", "$.entryPoint.type")]
    [InlineData("activities", "[\"NoNamespace\"]", "$.activities[0]")]
    [InlineData("activities", "[\"A.B\", \"A.B\"]", "$.activities[1]")]
    [InlineData("providers", "[\"bad id\"]", "$.providers[0]")]
    [InlineData("capabilities", "\"FileSystem\"", "$.capabilities")]
    [InlineData("dependencies", """[{ "id": "Tests.Fixture", "version": "1.0.0" }]""", "$.dependencies[0].id")]
    [InlineData("dependencies", """[{ "id": "A.B", "version": "latest" }]""", "$.dependencies[0].version")]
    [InlineData("dependencies", """[{ "id": "A.B" }]""", "$.dependencies[0].version")]
    public void InvalidField_IsReportedAtItsPath(string field, string value, string path)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(Manifests.Fixture())!.AsObject();
        json[field] = System.Text.Json.Nodes.JsonNode.Parse(value);

        var result = Read(json.ToJsonString());

        Assert.Null(result.Manifest);
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Path == path);
    }

    [Theory]
    [InlineData("activities", "[\"Core.Hijack\"]")]
    [InlineData("providers", "[\"core.Provider\"]")]
    public void ReservedCoreNamespace_IsRejected(string field, string value)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(Manifests.Fixture())!.AsObject();
        json[field] = System.Text.Json.Nodes.JsonNode.Parse(value);

        Assert.Contains(PluginDiagnosticCodes.NameConflict, ErrorCodes(Read(json.ToJsonString())));
    }

    [Fact]
    public void SeveralProblems_AreReportedTogether()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(Manifests.Fixture())!.AsObject();
        json["id"] = "x";
        json["version"] = "one";
        json["targetFramework"] = "java";

        var result = Read(json.ToJsonString());

        Assert.Equal(3, ErrorCodes(result).Count());
    }

    [Fact]
    public void UnknownFieldsAndCapabilities_AreWarnings_AndDoNotBlock()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(Manifests.Fixture(capabilities: """["Teleport"]"""))!.AsObject();
        json["homepage"] = "https://example.invalid";

        var result = Read(json.ToJsonString());

        Assert.NotNull(result.Manifest);
        Assert.Equal(2, result.Diagnostics.Count);
        Assert.All(result.Diagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
    }
}
