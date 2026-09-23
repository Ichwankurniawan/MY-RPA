namespace MyRPA.Architecture.Tests;

/// <summary>Locates the repository root (the directory containing MyRPA.sln) and its project files.</summary>
public static class Repository
{
    public static string Root { get; } = FindRoot();

    public static IReadOnlyList<ProjectFile> SourceProjects { get; } = LoadProjects("src");

    public static IReadOnlyList<ProjectFile> TestProjects { get; } = LoadProjects("tests");

    private static IReadOnlyList<ProjectFile> LoadProjects(string folder) =>
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
