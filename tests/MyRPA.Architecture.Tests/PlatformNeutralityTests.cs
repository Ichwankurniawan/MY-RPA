using System.Reflection;
using System.Runtime.Versioning;

namespace MyRPA.Architecture.Tests;

/// <summary>
/// Core stays platform-neutral and no Phase 1 project pulls in UI, browser, Windows automation, database, AI, MCP or
/// orchestrator technology (ADR-0004). Checked both in project files and in compiled assembly references.
/// </summary>
public sealed class PlatformNeutralityTests
{
    public static TheoryData<string> SourceProjectNames() => [.. ArchitectureRules.SourceProjects.Select(r => r.Name)];

    public static TheoryData<string> CompiledProjectNames() => [.. ArchitectureRules.SourceProjects.Where(r => r.Assembly is not null).Select(r => r.Name)];

    public static TheoryData<string> PlatformNeutralProjectNames() => [.. ArchitectureRules.SourceProjects.Where(r => !r.IsDesktopUi).Select(r => r.Name)];

    private static ArchitectureRules.ProjectRule Rule(string name) => ArchitectureRules.SourceProjects.Single(r => r.Name == name);

    private static ProjectFile Project(string name) => Repository.SourceProjects.Single(p => p.Name == name);

    [Fact]
    public void OnlyDesktopProjects_AreNotInspectedHere()
    {
        // Every platform-neutral project is compiled into this test run; only the WPF shell is checked in MyRPA.Studio.Tests.
        Assert.All(ArchitectureRules.SourceProjects.Where(r => r.Assembly is null), r => Assert.True(r.IsDesktopUi, r.Name));
    }

    [Fact]
    public void DesktopUi_IsOnlyInTheStudioShell()
    {
        Assert.Equal(["MyRPA.Studio"], ArchitectureRules.SourceProjects.Where(r => r.IsDesktopUi).Select(r => r.Name));

        var studio = Project("MyRPA.Studio");
        Assert.Equal("net10.0-windows", studio.TargetFramework);
        Assert.True(studio.UseWpf, "The Studio shell uses WPF.");
        Assert.False(studio.UseWindowsForms, "UseWindowsForms is not allowed.");
        Assert.Empty(studio.FrameworkReferences);
    }

    [Theory]
    [MemberData(nameof(CompiledProjectNames))]
    public void CompiledAssembly_TargetsPlainNet10(string name)
    {
        var assembly = Rule(name).Assembly!;

        Assert.Equal(".NETCoreApp,Version=v10.0", assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
        Assert.Null(assembly.GetCustomAttribute<TargetPlatformAttribute>()); // e.g. net10.0-windows
    }

    [Theory]
    [MemberData(nameof(PlatformNeutralProjectNames))]
    public void ProjectFile_DoesNotOverrideFrameworkOrEnableDesktopUi(string name)
    {
        var project = Project(name);

        Assert.Null(project.TargetFramework); // inherited from Directory.Build.props (net10.0)
        Assert.False(project.UseWpf, "UseWPF is not allowed.");
        Assert.False(project.UseWindowsForms, "UseWindowsForms is not allowed.");
        Assert.Empty(project.FrameworkReferences);
    }

    [Theory]
    [MemberData(nameof(SourceProjectNames))]
    public void PackageReferences_AreWithinAllowList(string name)
    {
        var disallowed = Project(name).PackageReferences.Except(Rule(name).AllowedPackages, StringComparer.OrdinalIgnoreCase);

        Assert.Empty(disallowed);
    }

    [Theory]
    [MemberData(nameof(SourceProjectNames))]
    public void PackageReferences_ContainNoForbiddenTechnology(string name)
    {
        var forbidden = Project(name).PackageReferences.Where(p => ArchitectureRules.MatchForbidden(p) is not null);

        Assert.Empty(forbidden);
    }

    [Theory]
    [MemberData(nameof(CompiledProjectNames))]
    public void CompiledReferences_ContainNoForbiddenTechnology(string name)
    {
        var rule = Rule(name);
        var forbidden = rule.Assembly!.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => ArchitectureRules.MatchForbidden(n) is { } prefix && !(rule.AllowsAspNetCore && prefix == "Microsoft.AspNetCore"));

        Assert.Empty(forbidden);
    }

    [Fact]
    public void AspNetCore_IsAllowedOnlyInTheServer()
    {
        // ADR-0022: ASP.NET Core only in server executables. The Web SDK adds it implicitly, so compiled references are checked.
        Assert.Equal(["MyRPA.Server"], ArchitectureRules.SourceProjects.Where(r => r.AllowsAspNetCore).Select(r => r.Name));
        Assert.Contains(Rule("MyRPA.Server").Assembly!.GetReferencedAssemblies(), a => a.Name!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.All(
            ArchitectureRules.SourceProjects.Where(r => r.Assembly is not null && !r.AllowsAspNetCore),
            r => Assert.DoesNotContain(r.Assembly!.GetReferencedAssemblies(), a => a.Name!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("MyRPA.Core")]
    [InlineData("MyRPA.Workflow")]
    public void CoreAndWorkflow_ReferenceOnlyTheBclAndAllowedProjects(string name)
    {
        var rule = Rule(name);
        var nonBcl = rule.Assembly!.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => !ArchitectureRules.IsBclAssembly(n) && !rule.AllowedProjects.Contains(n));

        Assert.Empty(nonBcl);
    }

    [Theory]
    [InlineData("MyRPA.Core")]
    [InlineData("MyRPA.Workflow")]
    public void CoreAndWorkflow_HaveNoPackages(string name) => Assert.Empty(Project(name).PackageReferences);

    [Fact]
    public void Contracts_ReferenceOnlyTheBcl_WithNoProjectsOrPackages()
    {
        // Wire contracts are shared with future agents and robots (ADR-0022): nothing but the BCL.
        var nonBcl = Rule("MyRPA.Contracts").Assembly!.GetReferencedAssemblies().Select(a => a.Name!).Where(n => !ArchitectureRules.IsBclAssembly(n));

        Assert.Empty(nonBcl);
        Assert.Empty(Project("MyRPA.Contracts").ProjectReferences);
        Assert.Empty(Project("MyRPA.Contracts").PackageReferences);
    }

    [Fact]
    public void OnlyCompositionRoots_ReferenceHosting()
    {
        var offenders = ArchitectureRules.SourceProjects
            .Where(r => !r.IsCompositionRoot)
            .Where(r => r.Assembly!.GetReferencedAssemblies().Any(a => a.Name == "Microsoft.Extensions.Hosting"
                || a.Name == "Microsoft.Extensions.Hosting.Abstractions"))
            .Select(r => r.Name);

        Assert.Empty(offenders);
    }

    [Fact]
    public void OnlyCompositionRoots_AreExecutables()
    {
        var executables = Repository.SourceProjects
            .Where(p => string.Equals(p.OutputType, "Exe", StringComparison.OrdinalIgnoreCase) || string.Equals(p.OutputType, "WinExe", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name);
        var roots = ArchitectureRules.SourceProjects.Where(r => r.IsCompositionRoot).Select(r => r.Name);

        Assert.Equal(roots.Order(StringComparer.Ordinal), executables.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Microsoft.Playwright")]
    [InlineData("FlaUI.UIA3")]
    [InlineData("PresentationFramework")]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite")]
    [InlineData("ModelContextProtocol")]
    [InlineData("System.Windows.Forms")]
    public void ForbiddenList_MatchesKnownTechnologies(string name) => Assert.NotNull(ArchitectureRules.MatchForbidden(name));

    [Theory]
    [InlineData("System.Runtime")]
    [InlineData("Microsoft.Extensions.Logging.Abstractions")]
    [InlineData("MyRPA.Core")]
    public void ForbiddenList_DoesNotMatchAllowedAssemblies(string name) => Assert.Null(ArchitectureRules.MatchForbidden(name));
}
