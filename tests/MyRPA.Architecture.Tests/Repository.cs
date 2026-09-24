namespace MyRPA.Architecture.Tests;

/// <summary>Locates the repository root (the directory containing MyRPA.sln) and its project files.</summary>
public static class Repository
{
    public static string Root { get; } = FindRoot();

    public static IReadOnlyList<ProjectFile> SourceProjects { get; } = LoadProjects("src");

    /// <summary>Test projects (excluding the plugin fixtures under tests/fixtures).</summary>
    public static IReadOnlyList<ProjectFile> TestProjects { get; } =
        [.. LoadProjects("tests").Where(p => !IsUnder(p.Path, Path.Combine("tests", "fixtures")))];

    /// <summary>Plugin projects: product plugins (plugins/), sample plugins and test fixture plugins (ADR-0014).</summary>
    public static IReadOnlyList<ProjectFile> PluginProjects { get; } =
        [.. LoadProjects("plugins"), .. LoadProjects(Path.Combine("samples", "plugins")), .. LoadProjects(Path.Combine("tests", "fixtures"))];

    /// <summary>Every project in the repository (src, tests, plugins, samples).</summary>
    public static IReadOnlyList<ProjectFile> AllProjects { get; } =
        [.. SourceProjects, .. LoadProjects("tests"), .. LoadProjects("plugins"), .. LoadProjects("samples")];

    private static bool IsUnder(string path, string relativeFolder) =>
        Path.GetRelativePath(Root, path).StartsWith(relativeFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static IReadOnlyList<ProjectFile> LoadProjects(string folder) =>
        !Directory.Exists(Path.Combine(Root, folder)) ? [] :
        [.. Directory.EnumerateFiles(Path.Combine(Root, folder), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(ProjectFile.Load)];

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MyRPA.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate MyRPA.sln above " + AppContext.BaseDirectory);
    }
}
