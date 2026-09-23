using System.Reflection;

namespace MyRPA.Architecture.Tests;

/// <summary>
/// The architecture rules as data. Changing a rule is an architecture decision: update the ADRs
/// (ADR-0003 dependency direction, ADR-0004 platform-neutral Core, ADR-0008 security) in the same change.
/// </summary>
public static class ArchitectureRules
{
    /// <summary>Every src project, its compiled assembly, and what it may depend on.</summary>
    public static IReadOnlyList<ProjectRule> SourceProjects { get; } =
    [
        new("MyRPA.Core", typeof(MyRPA.Core.Diagnostics.DiagnosticNames).Assembly,
            AllowedProjects: [],
            AllowedPackages: []),
        new("MyRPA.Workflow", typeof(MyRPA.Workflow.WorkflowDefinition).Assembly,
            AllowedProjects: ["MyRPA.Core"],
            AllowedPackages: []),
        new("MyRPA.Activities", typeof(MyRPA.Activities.ActivityCatalog).Assembly,
            AllowedProjects: ["MyRPA.Core"],
            AllowedPackages: ["Microsoft.Extensions.DependencyInjection.Abstractions"]),
        new("MyRPA.Runtime", typeof(MyRPA.Runtime.RuntimeServiceCollectionExtensions).Assembly,
            AllowedProjects: ["MyRPA.Core", "MyRPA.Workflow"],
            AllowedPackages: ["Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Logging.Abstractions"]),
        new("MyRPA.Storage", typeof(MyRPA.Storage.StorageServiceCollectionExtensions).Assembly,
            AllowedProjects: ["MyRPA.Core", "MyRPA.Workflow"],
            AllowedPackages: ["Microsoft.Extensions.DependencyInjection.Abstractions"]),
        new("MyRPA.Cli", typeof(MyRPA.Cli.CliApplication).Assembly,
            AllowedProjects: ["MyRPA.Core", "MyRPA.Workflow", "MyRPA.Activities", "MyRPA.Runtime", "MyRPA.Storage"],
            AllowedPackages: ["Microsoft.Extensions.Hosting"],
            IsCompositionRoot: true),
    ];

    /// <summary>Projects whose compiled references must be BCL-only (plus allowed MyRPA projects).</summary>
    public static IReadOnlySet<string> BclOnlyProjects { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "MyRPA.Core",
        "MyRPA.Workflow",
    };

    /// <summary>
    /// Assembly/package name prefixes that no Phase 1 src project may reference: UI frameworks, browser and Windows
    /// automation, WF4, databases, AI and MCP SDKs, orchestrator stacks (PRD 7.1, 10.1; ADR-0004).
    /// </summary>
    public static IReadOnlyList<string> ForbiddenPrefixes { get; } =
    [
        // UI frameworks
        "PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows", "System.Xaml",
        "Microsoft.WindowsDesktop", "Avalonia", "Microsoft.Maui",
        // Browser automation
        "Microsoft.Playwright", "Selenium", "PuppeteerSharp", "CefSharp", "Microsoft.Web.WebView2",
        // Windows UI Automation
        "FlaUI", "UIAutomationClient", "UIAutomationTypes", "Interop.UIAutomationClient",
        // Legacy workflow runtimes (ADR-0002)
        "System.Activities", "UiPath.Workflow", "CoreWf",
        // Databases / ORMs
        "Microsoft.Data.Sqlite", "System.Data.SQLite", "SQLitePCLRaw", "Microsoft.Data.SqlClient", "System.Data.SqlClient",
        "Microsoft.EntityFrameworkCore", "Npgsql", "Dapper", "LiteDB", "MongoDB",
        // AI and MCP SDKs
        "OpenAI", "Anthropic", "Azure.AI", "Microsoft.SemanticKernel", "Microsoft.Extensions.AI", "ModelContextProtocol",
        // Orchestration / messaging stacks
        "RabbitMQ", "MassTransit", "Grpc", "Microsoft.AspNetCore",
    ];

    /// <summary>Returns the forbidden prefix matched by <paramref name="name"/>, if any.</summary>
    public static string? MatchForbidden(string name) =>
        ForbiddenPrefixes.FirstOrDefault(p =>
            name.Equals(p, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase));

    /// <summary>BCL assemblies shipped with the .NET runtime.</summary>
    public static bool IsBclAssembly(string name) =>
        name is "System" or "netstandard" or "mscorlib"
        || name.StartsWith("System.", StringComparison.Ordinal);

    /// <summary>Which src projects each test project may reference (tests stay focused on their subject).</summary>
    public static IReadOnlyDictionary<string, string[]> TestProjectReferences { get; } = new Dictionary<string, string[]>
    {
        ["MyRPA.Core.Tests"] = ["MyRPA.Core"],
        ["MyRPA.Workflow.Tests"] = ["MyRPA.Workflow"],
        ["MyRPA.Activities.Tests"] = ["MyRPA.Activities"],
        ["MyRPA.Runtime.Tests"] = ["MyRPA.Runtime"],
        ["MyRPA.Integration.Tests"] = ["MyRPA.Cli"],
        ["MyRPA.Architecture.Tests"] = ["MyRPA.Core", "MyRPA.Workflow", "MyRPA.Activities", "MyRPA.Runtime", "MyRPA.Storage", "MyRPA.Cli"],
    };

    /// <summary>A src project and its dependency allow-lists.</summary>
    public sealed record ProjectRule(
        string Name,
        Assembly Assembly,
        string[] AllowedProjects,
        string[] AllowedPackages,
        bool IsCompositionRoot = false)
    {
        public override string ToString() => Name;
    }
}
