using System.Reflection;
using System.Text.Json;

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
        // ADR-0022: control-plane wire contracts; BCL only, no project references, no packages.
        new("MyRPA.Contracts", typeof(MyRPA.Contracts.Execution.ExecutionEventMessage).Assembly,
            AllowedProjects: [],
            AllowedPackages: []),
        // ADR-0022/0023: execution hosting shared by the server, agents and robots. Engine contracts only (not Runtime).
        new("MyRPA.Execution.Hosting", typeof(MyRPA.Execution.Hosting.ExecutionHost).Assembly,
            AllowedProjects: ["MyRPA.Core", "MyRPA.Workflow", "MyRPA.Contracts"],
            AllowedPackages: ["Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Logging.Abstractions"]),
        // ADR-0018: Studio logic is platform-neutral (reusable by a future web Studio); only MyRPA.Studio uses WPF.
        new("MyRPA.Studio.Core", typeof(MyRPA.Studio.Documents.WorkflowDraft).Assembly,
            AllowedProjects: ["MyRPA.Core", "MyRPA.Workflow"],
            AllowedPackages: ["CommunityToolkit.Mvvm", "Microsoft.Extensions.Logging.Abstractions"]),
        // ADR-0018: the desktop Studio shell is the only project that may use WPF (net10.0-windows). This project cannot
        // reference it (it targets plain net10.0), so its compiled-code rules run in MyRPA.Studio.Tests.
        new("MyRPA.Studio", Assembly: null,
            AllowedProjects: ["MyRPA.Core", "MyRPA.Workflow", "MyRPA.Activities", "MyRPA.Runtime", "MyRPA.Storage", "MyRPA.Plugins", "MyRPA.Studio.Core"],
            AllowedPackages: ["Microsoft.Extensions.Hosting", "CommunityToolkit.Mvvm"],
            IsCompositionRoot: true,
            IsDesktopUi: true),
        // ADR-0022/0025: the control-plane server; a composition root and the only src project allowed to use ASP.NET Core.
        new("MyRPA.Server", typeof(MyRPA.Server.ServerApplication).Assembly,
            AllowedProjects: ["MyRPA.Core", "MyRPA.Workflow", "MyRPA.Activities", "MyRPA.Runtime", "MyRPA.Storage", "MyRPA.Plugins", "MyRPA.Contracts", "MyRPA.Execution.Hosting"],
            AllowedPackages: [],
            IsCompositionRoot: true,
            AllowsAspNetCore: true),
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
        "MyRPA.Contracts",
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

    /// <summary>The control-plane layer, which the engine and built-in libraries never depend on (ADR-0022).</summary>
    public static IReadOnlyList<string> ControlPlaneProjects { get; } = ["MyRPA.Contracts", "MyRPA.Execution.Hosting"];

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

    /// <summary>
    /// npm packages the Web Studio (<c>web/studio</c>) must not use. ADR-0021 rejected dnd-kit; the designer uses plain
    /// pointer hit-testing and keyboard commands, so no drag-and-drop framework replaces it.
    /// </summary>
    public static IReadOnlyList<string> RejectedWebPackagePrefixes { get; } =
        ["@dnd-kit/", "react-dnd", "react-beautiful-dnd", "@hello-pangea/dnd", "@atlaskit/pragmatic-drag-and-drop", "react-draggable", "react-sortable", "sortablejs"];

    /// <summary>Rejected packages named by an npm <c>package.json</c> (dependency sections) or <c>package-lock.json</c> (<c>packages</c>).</summary>
    public static IReadOnlyList<string> FindRejectedWebPackages(string npmJson)
    {
        using var document = JsonDocument.Parse(npmJson);
        var root = document.RootElement;
        var names = new List<string>();
        foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
        {
            if (root.TryGetProperty(section, out var dependencies) && dependencies.ValueKind == JsonValueKind.Object)
            {
                names.AddRange(dependencies.EnumerateObject().Select(p => p.Name));
            }
        }

        if (root.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Object)
        {
            const string marker = "node_modules/";
            names.AddRange(packages.EnumerateObject()
                .Select(p => p.Name)
                .Where(n => n.Contains(marker, StringComparison.Ordinal))
                .Select(n => n[(n.LastIndexOf(marker, StringComparison.Ordinal) + marker.Length)..]));
        }

        return [.. names
            .Where(n => RejectedWebPackagePrefixes.Any(prefix => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }

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
        ["MyRPA.Architecture.Tests"] = ["MyRPA.Core", "MyRPA.Workflow", "MyRPA.Activities", "MyRPA.Runtime", "MyRPA.Storage", "MyRPA.Sdk", "MyRPA.Plugins", "MyRPA.Contracts", "MyRPA.Execution.Hosting", "MyRPA.Studio.Core", "MyRPA.Server", "MyRPA.Cli"],
        // The server is tested as it runs: real Kestrel on loopback, real engine and plugin host.
        ["MyRPA.Server.Tests"] = ["MyRPA.Server"],
        // Execution hosting runs the real engine with the built-in activities.
        ["MyRPA.Execution.Hosting.Tests"] = ["MyRPA.Execution.Hosting", "MyRPA.Contracts", "MyRPA.Runtime", "MyRPA.Activities"],
        // Studio logic is tested headless, with the real engine and built-in activities.
        ["MyRPA.Studio.Core.Tests"] = ["MyRPA.Studio.Core", "MyRPA.Activities", "MyRPA.Runtime", "MyRPA.Storage"],
        // The WPF shell: window smoke tests and the code rules on its compiled assembly.
        ["MyRPA.Studio.Tests"] = ["MyRPA.Studio"],
    };

    /// <summary>A banned API allowed in one type.</summary>
    public sealed record BannedApiExemption(string TypeName, string Api, string Reason);

    /// <summary>A src project and its dependency allow-lists.</summary>
    /// <param name="Name">Project name.</param>
    /// <param name="Assembly">Its compiled assembly; null for a desktop project this net10.0 test project cannot reference.</param>
    /// <param name="AllowedProjects">Projects it may reference.</param>
    /// <param name="AllowedPackages">Packages it may reference.</param>
    /// <param name="IsCompositionRoot">Whether it is an executable that composes the application.</param>
    /// <param name="IsDesktopUi">Whether it may target net10.0-windows and use WPF (ADR-0018).</param>
    /// <param name="AllowsAspNetCore">Whether it may reference ASP.NET Core (server executables only, ADR-0022).</param>
    public sealed record ProjectRule(
        string Name,
        Assembly? Assembly,
        string[] AllowedProjects,
        string[] AllowedPackages,
        bool IsCompositionRoot = false,
        bool IsDesktopUi = false,
        bool AllowsAspNetCore = false)
    {
        public override string ToString() => Name;
    }
}
