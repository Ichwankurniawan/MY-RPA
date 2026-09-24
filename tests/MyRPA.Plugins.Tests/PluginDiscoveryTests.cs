using MyRPA.Workflow.Validation;

namespace MyRPA.Plugins.Tests;

/// <summary>Discovery, manifest validation, compatibility, trust and dependency checks — before any plugin code runs.</summary>
public sealed class PluginDiscoveryTests
{
    private static IEnumerable<string> Codes(PluginSet set) =>
        set.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Code);

    [Fact]
    public async Task NoSources_LoadsNothing()
    {
        await using var set = await PluginTestHost.LoadAsync();

        Assert.Empty(set.Plugins);
        Assert.Empty(set.Diagnostics);
        Assert.False(set.HasRequiredFailures);
    }

    [Fact]
    public async Task ValidPlugin_IsLoaded()
    {
        using var staged = new StagedPlugin(Manifests.Fixture());
        await using var set = await PluginTestHost.LoadAsync(staged.Source());

        var plugin = Assert.Single(set.Plugins);
        Assert.Equal("Tests.Fixture", plugin.Manifest.Id.Value);
        Assert.Equal(64, plugin.Digest.Length);
        Assert.Empty(set.Diagnostics);
    }

    [Fact]
    public async Task DirectoryWithAssembliesButNoManifest_IsNotAPlugin()
    {
        using var staged = new StagedPlugin(Manifests.Fixture(), withManifest: false);
        await using var set = await PluginTestHost.LoadAsync(staged.Source());

        Assert.Empty(set.Plugins);
        Assert.Equal([PluginDiagnosticCodes.ManifestMissing], Codes(set));
        Assert.True(set.HasRequiredFailures);
    }

    [Fact]
    public async Task MissingOrRelativeDirectory_IsRejected()
    {
        await using var set = await PluginTestHost.LoadAsync(
            new PluginSource { Directory = Path.Combine(Path.GetTempPath(), "myrpa-does-not-exist-" + Guid.NewGuid().ToString("N")) },
            new PluginSource { Directory = "relative/plugin" });

        Assert.Equal([PluginDiagnosticCodes.DirectoryInvalid, PluginDiagnosticCodes.DirectoryInvalid], Codes(set));
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("1.1")]
    public async Task IncompatibleSdk_IsRejected(string sdkVersion)
    {
        using var staged = new StagedPlugin(Manifests.Fixture(sdkVersion: sdkVersion));
        await using var set = await PluginTestHost.LoadAsync(staged.Source());

        Assert.Equal([PluginDiagnosticCodes.IncompatibleSdk], Codes(set));
    }

    [Fact]
    public async Task NewerFramework_IsRejected()
    {
        using var staged = new StagedPlugin(Manifests.Fixture(targetFramework: "net99.0"));
        await using var set = await PluginTestHost.LoadAsync(staged.Source());

        Assert.Equal([PluginDiagnosticCodes.IncompatibleFramework], Codes(set));
    }

    [Fact]
    public async Task DeniedCapability_IsRejected()
    {
        using var staged = new StagedPlugin(Manifests.Fixture(capabilities: """["Process", "FileSystem"]"""));
        var options = new PluginHostOptions();
        options.DeniedCapabilities.Add(PluginCapabilities.Process);

        await using var set = await PluginTestHost.LoadAsync(options, staged.Source());

        Assert.Equal([PluginDiagnosticCodes.CapabilityDenied], Codes(set));
    }

    [Fact]
    public async Task MissingEntryAssembly_IsRejectedBeforeLoading()
    {
        using var staged = new StagedPlugin(Manifests.Fixture(assembly: "Missing.dll"));
        await using var set = await PluginTestHost.LoadAsync(staged.Source());

        Assert.Equal([PluginDiagnosticCodes.EntryPointInvalid], Codes(set));
    }

    [Fact]
    public async Task DuplicatePluginId_KeepsTheFirst()
    {
        using var first = new StagedPlugin(Manifests.Fixture());
        using var second = new StagedPlugin(Manifests.Empty("tests.FIXTURE"));
        await using var set = await PluginTestHost.LoadAsync(first.Source(), second.Source());

        Assert.Equal(first.Directory, Assert.Single(set.Plugins).Directory);
        Assert.Equal([PluginDiagnosticCodes.DuplicatePlugin], Codes(set));
    }

    [Fact]
    public async Task ActivityDeclaredByTwoPlugins_RejectsTheSecond()
    {
        using var first = new StagedPlugin(Manifests.Fixture());
        using var second = new StagedPlugin(Manifests.Fixture(id: "Tests.Copy"));
        await using var set = await PluginTestHost.LoadAsync(first.Source(), second.Source());

        Assert.Single(set.Plugins);
        Assert.Equal([PluginDiagnosticCodes.NameConflict], Codes(set));
    }

    [Fact]
    public async Task Integrity_PinnedDigestMustMatch()
    {
        using var staged = new StagedPlugin(Manifests.Fixture());
        string digest;
        await using (var probe = await PluginTestHost.LoadAsync(staged.Source()))
        {
            digest = Assert.Single(probe.Plugins).Digest;
        }

        await using (var pinned = await PluginTestHost.LoadAsync(staged.Source(sha256: digest.ToUpperInvariant())))
        {
            Assert.Single(pinned.Plugins);
        }

        // Any change to any file in the directory changes the digest.
        File.AppendAllText(Path.Combine(staged.Directory, "MyRPA.Tests.FixturePlugin.xml"), " ");
        await using var tampered = await PluginTestHost.LoadAsync(staged.Source(sha256: digest));

        Assert.Empty(tampered.Plugins);
        Assert.Equal([PluginDiagnosticCodes.IntegrityMismatch], Codes(tampered));
    }

    [Fact]
    public async Task RequireIntegrity_RejectsUnpinnedPlugins()
    {
        using var staged = new StagedPlugin(Manifests.Fixture());
        var options = new PluginHostOptions { RequireIntegrity = true };

        await using var set = await PluginTestHost.LoadAsync(options, staged.Source());

        Assert.Equal([PluginDiagnosticCodes.IntegrityPinRequired], Codes(set));
        Assert.Contains("Current digest:", set.Diagnostics[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OptionalPluginFailure_IsADiagnostic_AndOthersStillLoad()
    {
        using var broken = new StagedPlugin(Manifests.Fixture(id: "Tests.Broken", sdkVersion: "9.0"));
        using var good = new StagedPlugin(Manifests.Fixture());
        await using var set = await PluginTestHost.LoadAsync(broken.Source(required: false), good.Source());

        Assert.Equal("Tests.Fixture", Assert.Single(set.Plugins).Manifest.Id.Value);
        Assert.Equal([PluginDiagnosticCodes.IncompatibleSdk], Codes(set));
        Assert.False(set.HasRequiredFailures);
    }

    [Fact]
    public async Task Dependencies_AreLoadedFirst()
    {
        using var dependent = new StagedPlugin(Manifests.Empty("Tests.App", """[{ "id": "Tests.Base", "version": "1.0.0" }]"""));
        using var dependency = new StagedPlugin(Manifests.Empty("Tests.Base", version: "1.4.2"));
        await using var set = await PluginTestHost.LoadAsync(dependent.Source(), dependency.Source());

        Assert.Equal(["Tests.Base", "Tests.App"], set.Plugins.Select(p => p.Manifest.Id.Value));
    }

    [Theory]
    [InlineData("""[{ "id": "Tests.Nowhere", "version": "1.0.0" }]""", PluginDiagnosticCodes.DependencyMissing)]
    [InlineData("""[{ "id": "Tests.Base", "version": "1.5.0" }]""", PluginDiagnosticCodes.DependencyVersion)]
    [InlineData("""[{ "id": "Tests.Base", "version": "0.9.0" }]""", PluginDiagnosticCodes.DependencyVersion)]
    public async Task UnsatisfiedDependency_IsRejected(string dependencies, string code)
    {
        using var dependent = new StagedPlugin(Manifests.Empty("Tests.App", dependencies));
        using var dependency = new StagedPlugin(Manifests.Empty("Tests.Base", version: "1.4.2"));
        await using var set = await PluginTestHost.LoadAsync(dependent.Source(), dependency.Source());

        Assert.Equal(["Tests.Base"], set.Plugins.Select(p => p.Manifest.Id.Value));
        Assert.Equal([code], Codes(set));
    }

    [Fact]
    public async Task DependencyCycle_RejectsEveryPluginInIt()
    {
        using var a = new StagedPlugin(Manifests.Empty("Tests.A", """[{ "id": "Tests.B", "version": "1.0.0" }]"""));
        using var b = new StagedPlugin(Manifests.Empty("Tests.B", """[{ "id": "Tests.A", "version": "1.0.0" }]"""));
        await using var set = await PluginTestHost.LoadAsync(a.Source(), b.Source());

        Assert.Empty(set.Plugins);
        Assert.Equal([PluginDiagnosticCodes.DependencyCycle, PluginDiagnosticCodes.DependencyCycle], Codes(set));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectedDependency_RejectsItsDependents(bool failsAtLoadTime)
    {
        // Rejected either before loading (incompatible SDK) or while loading (Initialize throws).
        var baseManifest = failsAtLoadTime
            ? Manifests.Fixture("Tests.Base", "MyRPA.Tests.FixturePlugin.ThrowingInitializePlugin", activities: [], providers: [])
            : Manifests.Fixture("Tests.Base", "MyRPA.Tests.FixturePlugin.EmptyPlugin", sdkVersion: "7.0");
        using var dependency = new StagedPlugin(baseManifest);
        using var dependent = new StagedPlugin(Manifests.Empty("Tests.App", """[{ "id": "Tests.Base", "version": "1.0.0" }]"""));
        await using var set = await PluginTestHost.LoadAsync(dependency.Source(), dependent.Source());

        Assert.Empty(set.Plugins);
        Assert.Contains(PluginDiagnosticCodes.DependencyFailed, Codes(set));
    }
}
