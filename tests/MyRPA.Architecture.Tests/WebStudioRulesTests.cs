namespace MyRPA.Architecture.Tests;

/// <summary>Rules for the Web Studio (<c>web/studio</c>, ADR-0021), an npm project outside the .NET solution.</summary>
public sealed class WebStudioRulesTests
{
    private static string StudioFolder => Path.Combine(Repository.Root, "web", "studio");

    [Theory]
    [InlineData("package.json")]
    [InlineData("package-lock.json")]
    public void WebStudio_UsesNoDragAndDropFramework(string file)
    {
        // ADR-0021: dnd-kit is rejected, and no other drag-and-drop framework replaces it. The lock file covers
        // packages that arrive indirectly.
        var path = Path.Combine(StudioFolder, file);
        Assert.True(File.Exists(path), $"web/studio/{file} is missing.");

        Assert.Empty(ArchitectureRules.FindRejectedWebPackages(File.ReadAllText(path)));
    }

    [Fact]
    public void RejectedWebPackageDetector_FlagsKnownBadManifestsAndLockFiles()
    {
        Assert.Equal(["@dnd-kit/core"], ArchitectureRules.FindRejectedWebPackages("""{ "dependencies": { "react": "19.2.8", "@dnd-kit/core": "6.3.0" } }"""));
        Assert.Equal(["react-dnd"], ArchitectureRules.FindRejectedWebPackages("""{ "devDependencies": { "react-dnd": "16.0.1" } }"""));
        Assert.Equal(
            ["@dnd-kit/utilities", "sortablejs"],
            ArchitectureRules.FindRejectedWebPackages("""{ "packages": { "": {}, "node_modules/react": {}, "node_modules/x/node_modules/@dnd-kit/utilities": {}, "node_modules/sortablejs": {} } }"""));
        Assert.Empty(ArchitectureRules.FindRejectedWebPackages("""{ "dependencies": { "react": "19.2.8", "react-dom": "19.2.8" } }"""));
    }
}
