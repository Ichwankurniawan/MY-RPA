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
        // ADR-0010 amends ADR-0003: Activities implements the execution contracts that live in Workflow.
        new("MyRPA.Activities", typeof(MyRPA.Activities.ActivityCatalog).Assembly,
            AllowedProjects: ["MyRPA.Core", "MyRPA.Workflow"],
            AllowedPackages: ["Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Logging.Abstractions"]),
        new("MyRPA.Runtime", typeof(MyRPA.Runtime.RuntimeServiceCollectionExtensions).Assembly,
            AllowedProjects: ["MyRPA.Core", "MyRPA.Workflow"],
            AllowedPackages: ["Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Logging.Abstractions"]),
        new("MyRPA.Storage", typeof(MyRPA.Storage.StorageServiceCollectionExtensions).Assembly,
            AllowedProjects: ["MyRPA.Core", "MyRPA.Workflow"],
            AllowedPackages: ["Microsoft.Extensions.DependencyInjection.Abstractions"]),
        // ADR-0013: the Automation SDK is what plugins compile against; contracts only.
        new("MyRPA.Sdk", typeof(MyRPA.Sdk.AutomationSdk).Assembly,
            AllowedProjects: ["MyRPA.Core", "MyRPA.Workflow"],
            AllowedPackages: []),
        // ADR-0014: the plugin host is used by composition roots only; the engine never references it.
        new("MyRPA.Plugins", typeof(MyRPA.Plugins.PluginLoader).Assembly,
            AllowedProjects: ["MyRPA.Core", "MyRPA.Workflow", "MyRPA.Sdk", "MyRPA.Activities"],
            AllowedPackages: ["Microsoft.Extensions.DependencyInjection.Abstractions"]),
        new("MyRPA.Cli", typeof(MyRPA.Cli.CliApplication).Assembly,
            AllowedProjects: ["MyRPA.Core", "MyRPA.Workflow", "MyRPA.Activities", "MyRPA.Runtime", "MyRPA.Storage", "MyRPA.Sdk", "MyRPA.Plugins"],
            AllowedPackages: ["Microsoft.Extensions.Hosting"],
            IsCompositionRoot: true),
    ];

    /// <summary>Projects whose compiled references must be BCL-only (plus allowed MyRPA projects).</summary>
    public static IReadOnlySet<string> BclOnlyProjects { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "MyRPA.Core",
        "MyRPA.Workflow",
        "MyRPA.Sdk",
    };

    /// <summary>
    /// Banned APIs that one specific type may call (ADR-0014). The plugin load context is the single place that loads
    /// assemblies and resolves a manifest-named entry type inside a plugin's own assembly.
    /// </summary>
    public static IReadOnlyList<BannedApiExemption> BannedApiExemptions { get; } =
    [
        new("MyRPA.Plugins.Loading.PluginLoadContext", "AssemblyLoadContext.LoadFrom", "loads verified plugin assemblies into the plugin's own context"),
        new("MyRPA.Plugins.Loading.PluginLoadContext", "Assembly.GetType(string)", "finds the manifest's entry type inside the plugin's entry assembly"),
    ];

    /// <summary>Projects the engine must never depend on (ADR-0013, ADR-0014).</summary>
    public static IReadOnlyList<string> PluginSystemProjects { get; } = ["MyRPA.Sdk", "MyRPA.Plugins"];

    /// <summary>The src project a plugin project (plugins, samples/plugins, tests/fixtures) may reference (ADR-0014).</summary>
    public static IReadOnlyList<string> PluginProjectAllowedReferences { get; } = ["MyRPA.Sdk"];

    /// <summary>
    /// Technology packages that are forbidden in src but allowed in exactly one provider plugin (ADR-0017). A technology
    /// lives behind its plugin boundary and nowhere else.
    /// </summary>
    public static IReadOnlyDictionary<string, string> TechnologyPackageOwners { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Playwright"] = "MyRPA.Browser.Playwright",
    };

    /// <summary>Returns <see langword="true"/> when <paramref name="violation"/> (from the IL scan) is exempted.</summary>
    public static bool IsExempt(string violation) =>
        BannedApiExemptions.Any(e =>
            (violation.StartsWith(e.TypeName + ".", StringComparison.Ordinal) || violation.StartsWith(e.TypeName + "+", StringComparison.Ordinal))
            && violation.Contains(e.Api, StringComparison.Ordinal));

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
        // Built-in activities are tested through the real engine (not through test doubles).
        ["MyRPA.Activities.Tests"] = ["MyRPA.Activities", "MyRPA.Runtime"],
        // The engine is tested with test-only activities, proving it does not depend on the built-in library.
        ["MyRPA.Runtime.Tests"] = ["MyRPA.Runtime"],
        ["MyRPA.Storage.Tests"] = ["MyRPA.Storage"],
        // SDK contracts are tested through the real engine and built-in activities.
        ["MyRPA.Sdk.Tests"] = ["MyRPA.Sdk", "MyRPA.Activities", "MyRPA.Runtime"],
        ["MyRPA.Plugins.Tests"] = ["MyRPA.Plugins", "MyRPA.Runtime"],
        // The browser plugin is tested through the real plugin host (it is only built, never compiled against).
        ["MyRPA.Browser.Playwright.Tests"] = ["MyRPA.Plugins", "MyRPA.Runtime"],
        ["MyRPA.Integration.Tests"] = ["MyRPA.Cli"],
        ["MyRPA.Architecture.Tests"] = ["MyRPA.Core", "MyRPA.Workflow", "MyRPA.Activities", "MyRPA.Runtime", "MyRPA.Storage", "MyRPA.Sdk", "MyRPA.Plugins", "MyRPA.Cli"],
    };

    /// <summary>A banned API allowed in one type.</summary>
    public sealed record BannedApiExemption(string TypeName, string Api, string Reason);

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
