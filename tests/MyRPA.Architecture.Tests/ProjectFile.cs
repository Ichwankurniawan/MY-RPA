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

        static string NameOf(string path) => System.IO.Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
        static bool IsBuildOnly(XElement e) =>
            string.Equals((string?)e.Attribute("ReferenceOutputAssembly") ?? e.Element("ReferenceOutputAssembly")?.Value, "false", StringComparison.OrdinalIgnoreCase);

        var references = document.Descendants("ProjectReference").Where(e => e.Attribute("Include") is not null).ToList();
        ProjectReferencePaths = [.. references.Where(e => !IsBuildOnly(e)).Select(e => (string)e.Attribute("Include")!)];
        ProjectReferences = [.. ProjectReferencePaths.Select(NameOf)];
        BuildOnlyReferences = [.. references.Where(IsBuildOnly).Select(e => NameOf((string)e.Attribute("Include")!))];
        PackageReferences = Values("PackageReference");
        FrameworkReferences = Values("FrameworkReference");
        TargetFramework = Property("TargetFramework") ?? Property("TargetFrameworks");
        OutputType = Property("OutputType");
        UseWpf = string.Equals(Property("UseWPF"), "true", StringComparison.OrdinalIgnoreCase);
        UseWindowsForms = string.Equals(Property("UseWindowsForms"), "true", StringComparison.OrdinalIgnoreCase);
        EnableDynamicLoading = string.Equals(Property("EnableDynamicLoading"), "true", StringComparison.OrdinalIgnoreCase);
    }

    public string Path { get; }

    public string Name { get; }

    public IReadOnlyList<string> ProjectReferencePaths { get; }

    /// <summary>Referenced projects whose output assembly is referenced (compile-time dependencies).</summary>
    public IReadOnlyList<string> ProjectReferences { get; }

    /// <summary>Projects referenced with ReferenceOutputAssembly=false: built first, never compiled against.</summary>
    public IReadOnlyList<string> BuildOnlyReferences { get; }

    public IReadOnlyList<string> PackageReferences { get; }

    public IReadOnlyList<string> FrameworkReferences { get; }

    /// <summary>TargetFramework(s) declared in the project file itself (null when inherited).</summary>
    public string? TargetFramework { get; }

    public string? OutputType { get; }

    public bool UseWpf { get; }

    public bool UseWindowsForms { get; }

    public bool EnableDynamicLoading { get; }

    public static ProjectFile Load(string path) => new(path, XDocument.Load(path));

    public override string ToString() => Name;
}
