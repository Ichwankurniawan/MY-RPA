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

    [Fact]
    public void ProjectGraph_HasNoCycles()
    {
        var graph = Repository.SourceProjects.Concat(Repository.TestProjects)
            .ToDictionary(p => p.Name, p => p.ProjectReferences, StringComparer.Ordinal);

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
