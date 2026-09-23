using System.Xml.Linq;

namespace MyRPA.Architecture.Tests;

/// <summary>Minimal read-only view of an SDK-style .csproj file.</summary>
public sealed class ProjectFile
{
    private ProjectFile(string path, XDocument document)
    {
        Path = path;
        Name = System.IO.Path.GetFileNameWithoutExtension(path);

        string[] Values(string element, string attribute = "Include") =>
            [.. document.Descendants(element).Select(e => (string?)e.Attribute(attribute)).OfType<string>()];

        string? Property(string name) => document.Descendants(name).Select(e => e.Value.Trim()).FirstOrDefault();

        ProjectReferencePaths = Values("ProjectReference");
        ProjectReferences = [.. ProjectReferencePaths.Select(p => System.IO.Path.GetFileNameWithoutExtension(p.Replace('\\', '/')))];
        PackageReferences = Values("PackageReference");
        FrameworkReferences = Values("FrameworkReference");
        TargetFramework = Property("TargetFramework") ?? Property("TargetFrameworks");
        OutputType = Property("OutputType");
        UseWpf = string.Equals(Property("UseWPF"), "true", StringComparison.OrdinalIgnoreCase);
        UseWindowsForms = string.Equals(Property("UseWindowsForms"), "true", StringComparison.OrdinalIgnoreCase);
    }

    public string Path { get; }

    public string Name { get; }

    public IReadOnlyList<string> ProjectReferencePaths { get; }

    public IReadOnlyList<string> ProjectReferences { get; }

    public IReadOnlyList<string> PackageReferences { get; }

    public IReadOnlyList<string> FrameworkReferences { get; }

    /// <summary>TargetFramework(s) declared in the project file itself (null when inherited).</summary>
    public string? TargetFramework { get; }

    public string? OutputType { get; }

    public bool UseWpf { get; }

    public bool UseWindowsForms { get; }

    public static ProjectFile Load(string path) => new(path, XDocument.Load(path));

    public override string ToString() => Name;
}
