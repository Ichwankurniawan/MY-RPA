namespace MyRPA.Architecture.Tests;

/// <summary>Project-level dependency direction (ADR-0003).</summary>
public sealed class ProjectGraphTests
{
    public static TheoryData<string> SourceProjectNames() => [.. ArchitectureRules.SourceProjects.Select(r => r.Name)];

    private static ProjectFile Project(string name) => Repository.SourceProjects.Single(p => p.Name == name);

    private static ArchitectureRules.ProjectRule Rule(string name) => ArchitectureRules.SourceProjects.Single(r => r.Name == name);

    [Fact]
    public void EverySourceProject_HasAnArchitectureRule()
    {
        // A new project must be added to ArchitectureRules (and ADR-0003) before it can be built into the solution.
        var onDisk = Repository.SourceProjects.Select(p => p.Name).Order(StringComparer.Ordinal);
        var ruled = ArchitectureRules.SourceProjects.Select(r => r.Name).Order(StringComparer.Ordinal);

        Assert.Equal(ruled, onDisk);
    }

    [Theory]
    [MemberData(nameof(SourceProjectNames))]
    public void ProjectReferences_AreWithinAllowList(string name)
    {
        var disallowed = Project(name).ProjectReferences.Except(Rule(name).AllowedProjects, StringComparer.Ordinal);

        Assert.Empty(disallowed);
    }

    [Theory]
    [MemberData(nameof(SourceProjectNames))]
    public void SourceProjects_NeverReferenceTestProjects(string name)
    {
        var testNames = Repository.TestProjects.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(Project(name).ProjectReferences, testNames.Contains);
    }

    [Fact]
    public void NothingReferencesACompositionRoot()
    {
        var roots = ArchitectureRules.SourceProjects.Where(r => r.IsCompositionRoot).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var offenders = Repository.SourceProjects.Where(p => p.ProjectReferences.Any(roots.Contains)).Select(p => p.Name);

        Assert.Empty(offenders);
    }

    [Fact]
    public void Runtime_DoesNotDependOnActivityLibrary()
    {
        // The engine must not depend on a specific activity library (ADR-0003 decision 2).
        Assert.DoesNotContain("MyRPA.Activities", Project("MyRPA.Runtime").ProjectReferences);
    }

    [Theory]
    [InlineData("MyRPA.Core")]
    [InlineData("MyRPA.Workflow")]
    [InlineData("MyRPA.Activities")]
    [InlineData("MyRPA.Runtime")]
    [InlineData("MyRPA.Storage")]
    public void Engine_NeverDependsOnThePluginSystem(string name)
    {
        // The engine and built-in libraries work without the SDK or the plugin host (ADR-0013, ADR-0014).
        Assert.Empty(Project(name).ProjectReferences.Intersect(ArchitectureRules.PluginSystemProjects, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("MyRPA.Core")]
    [InlineData("MyRPA.Workflow")]
    [InlineData("MyRPA.Activities")]
    [InlineData("MyRPA.Runtime")]
    [InlineData("MyRPA.Storage")]
    [InlineData("MyRPA.Sdk")]
    [InlineData("MyRPA.Plugins")]
    public void Engine_NeverDependsOnTheControlPlane(string name)
    {
        // The engine exposes the per-run observer (ADR-0023); hosting consumes it, never the other way round (ADR-0022).
        Assert.Empty(Project(name).ProjectReferences.Intersect(ArchitectureRules.ControlPlaneProjects, StringComparer.Ordinal));
    }

    [Fact]
    public void PluginProjects_Exist()
    {
        Assert.Contains(Repository.PluginProjects, p => p.Name == "MyRPA.Samples.DemoPlugin");
    }

    public static TheoryData<string> PluginProjectNames() => [.. Repository.PluginProjects.Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(PluginProjectNames))]
    public void PluginProjects_ReferenceOnlyTheSdk(string name)
    {
        // A plugin compiles against the SDK only (plus its own private libraries), never against the engine or the host.
        var project = Repository.PluginProjects.Single(p => p.Name == name);
        var pluginNames = Repository.PluginProjects.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var disallowed = project.ProjectReferences
            .Where(r => !pluginNames.Contains(r))
            .Except(ArchitectureRules.PluginProjectAllowedReferences, StringComparer.Ordinal);

        Assert.Empty(disallowed);
        Assert.All(
            project.PackageReferences.Where(p => !(ArchitectureRules.TechnologyPackageOwners.TryGetValue(p, out var owner) && owner == name)),
            p => Assert.Null(ArchitectureRules.MatchForbidden(p)));
    }

    [Fact]
    public void TechnologyPackages_AreReferencedOnlyByTheirPlugin()
    {
        // Playwright (and future technology SDKs) must stay behind the provider plugin: no src, test, sample or other plugin
        // project may reference them (ADR-0017).
        var offenders = Repository.AllProjects
            .SelectMany(p => p.PackageReferences.Select(package => (Project: p.Name, Package: package)))
            .Where(x => ArchitectureRules.TechnologyPackageOwners.TryGetValue(x.Package, out var owner) && owner != x.Project)
            .Select(x => $"{x.Project} -> {x.Package}");

        Assert.Empty(offenders);
    }

    [Fact]
    public void BrowserPlugin_IsAPluginProject_WithPlaywright()
    {
        var browser = Assert.Single(Repository.PluginProjects, p => p.Name == "MyRPA.Browser.Playwright");

        Assert.Contains("Microsoft.Playwright", browser.PackageReferences);
        Assert.True(browser.EnableDynamicLoading);
    }

    [Theory]
    [MemberData(nameof(PluginProjectNames))]
    public void PluginEntryProjects_EnableDynamicLoading(string name)
    {
        // Plugins with a manifest are loaded through AssemblyDependencyResolver, which needs the .deps.json that
        // EnableDynamicLoading produces.
        var project = Repository.PluginProjects.Single(p => p.Name == name);
        if (File.Exists(Path.Combine(Path.GetDirectoryName(project.Path)!, "myrpa-plugin.json")) || project.ProjectReferences.Any(r => Repository.PluginProjects.Any(p => p.Name == r)))
        {
            Assert.True(project.EnableDynamicLoading, $"{name} must set EnableDynamicLoading.");
        }
    }

    [Theory]
    [MemberData(nameof(TestProjectNames))]
    public void TestProjects_BuildOnlyReferencesArePluginProjects(string name)
    {
        // Tests may build a plugin (to load it through the plugin host) but must never compile against it: loading it into
        // the default context would defeat the isolation being tested.
        var project = Repository.TestProjects.Single(p => p.Name == name);
        var pluginNames = Repository.PluginProjects.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Assert.All(project.BuildOnlyReferences, r => Assert.Contains(r, pluginNames));
        Assert.DoesNotContain(project.ProjectReferences, pluginNames.Contains);
    }

    [Fact]
    public void ProjectGraph_HasNoCycles()
    {
        var graph = Repository.SourceProjects.Concat(Repository.TestProjects).Concat(Repository.PluginProjects)
            .ToDictionary(p => p.Name, p => (IReadOnlyList<string>)[.. p.ProjectReferences, .. p.BuildOnlyReferences], StringComparer.Ordinal);

        var cycle = FindCycle(graph);

        Assert.True(cycle is null, cycle is null ? null : "Cycle: " + string.Join(" -> ", cycle));
    }

    [Fact]
    public void FindCycle_DetectsCycle()
    {
        // Guards the cycle detector itself.
        var graph = new Dictionary<string, IReadOnlyList<string>>
        {
            ["A"] = ["B"],
            ["B"] = ["C"],
            ["C"] = ["A"],
        };

        Assert.NotNull(FindCycle(graph));
    }

    [Theory]
    [MemberData(nameof(TestProjectNames))]
    public void TestProjects_ReferenceOnlyTheirSubjects(string name)
    {
        var project = Repository.TestProjects.Single(p => p.Name == name);

        Assert.True(ArchitectureRules.TestProjectReferences.TryGetValue(name, out var allowed), $"No rule for test project {name}.");
        Assert.Empty(project.ProjectReferences.Except(allowed, StringComparer.Ordinal));
    }

    public static TheoryData<string> TestProjectNames() => [.. Repository.TestProjects.Select(p => p.Name)];

    private static List<string>? FindCycle(IReadOnlyDictionary<string, IReadOnlyList<string>> graph)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = visiting, 2 = done
        var path = new List<string>();

        List<string>? Visit(string node)
        {
            if (state.TryGetValue(node, out var s))
            {
                return s == 1 ? [.. path.SkipWhile(n => n != node), node] : null;
            }

            state[node] = 1;
            path.Add(node);
            foreach (var next in graph.TryGetValue(node, out var edges) ? edges : [])
            {
                if (Visit(next) is { } cycle)
                {
                    return cycle;
                }
            }

            path.RemoveAt(path.Count - 1);
            state[node] = 2;
            return null;
        }

        return graph.Keys.Select(Visit).FirstOrDefault(c => c is not null);
    }
}
