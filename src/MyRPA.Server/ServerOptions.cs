namespace MyRPA.Server;

/// <summary>A registered project: a folder whose workflow files the server exposes (ADR-0025).</summary>
/// <param name="Name">Project name (the folder name).</param>
/// <param name="Root">Full path of the folder.</param>
/// <param name="InRoot">A folder of the projects folder (ADR-0046), which the Studio may delete; false for a <c>--project</c>.</param>
internal sealed record ProjectRoot(string Name, string Root, bool InRoot = false);

/// <summary>Server configuration, from the command line (local mode).</summary>
internal sealed record ServerOptions
{
    /// <summary>Registered projects (<c>--project</c>).</summary>
    public IReadOnlyList<ProjectRoot> Projects { get; init; } = [];

    /// <summary>
    /// The projects folder (<c>--projects-root</c>, ADR-0046): every folder in it is a project, New project creates one
    /// there and Delete moves one to its <c>.trash</c>. Null: only the <c>--project</c> folders, and no project is created.
    /// </summary>
    public string? ProjectsRoot { get; init; }

    /// <summary>Plugin directories named on the command line (required plugins).</summary>
    public IReadOnlyList<string> PluginDirectories { get; init; } = [];

    /// <summary>Plugin configuration file (ADR-0019).</summary>
    public string? PluginConfiguration { get; init; }

    /// <summary>The built Web Studio (<c>web/studio/dist</c>), served at <c>/</c> (ADR-0022); null serves no UI.</summary>
    public string? WebRoot { get; init; }

    /// <summary>A workflow the Studio opens after connecting (<c>--open</c>, like the WPF Studio's file argument).</summary>
    public OpenWorkflow? Open { get; init; }

    /// <summary>Loopback port; 0 picks a free one.</summary>
    public int Port { get; init; } = 5310;

    /// <summary>
    /// Recording browsers without a window (<c>--recorder-headless</c>). For automated tests only (CI has no display); a
    /// person records in a visible browser (ADR-0039).
    /// </summary>
    public bool RecorderHeadless { get; init; }

    /// <summary>
    /// A DevTools port for recording browsers (<c>--recorder-debugging-port</c>), so an end-to-end test can act as the
    /// user (ADR-0039). For automated tests only.
    /// </summary>
    public int? RecorderDebuggingPort { get; init; }

    /// <summary>Largest accepted request body (documents are at most a few hundred KB; ADR-0012 caps files at 5 MB).</summary>
    public long MaxRequestBodyBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>How long the one-time start token is valid.</summary>
    public TimeSpan StartTokenLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Open event streams (one per browser tab) the server keeps.</summary>
    public int MaxStreams { get; init; } = 32;

    /// <summary>Runs one stream may subscribe to.</summary>
    public int MaxSubscriptionsPerStream { get; init; } = 64;

    /// <summary>Events queued for one stream connection; when it is full, the run's events wait for the client.</summary>
    public int StreamQueueCapacity { get; init; } = 4096;

    /// <summary>
    /// How long the queue may stay full without the client taking an event; then the client counts as slow, is
    /// disconnected and resumes by cursor (ADR-0024).
    /// </summary>
    public TimeSpan SlowClientTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a disconnected stream is kept for reconnection.</summary>
    public TimeSpan StreamRetention { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Keep-alive comment interval on idle streams.</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Execution limits (concurrency, replay buffer, retention).</summary>
    public Action<Execution.Hosting.ExecutionHostOptions>? ConfigureHosting { get; init; }

    /// <summary>
    /// The Server's data folder (<c>--data</c>, ADR-0050): the orchestrator's store lives there. Null: no orchestrator
    /// (publishing, jobs, Runners and schedules are unavailable; the Studio's local runs still work).
    /// </summary>
    public string? DataDirectory { get; init; }

    /// <summary>
    /// The Runner listener (<c>--runner-url</c>, ADR-0051): an address remote Runners connect to, separate from the
    /// loopback Studio listener. It serves only the Runner protocol and the token-authenticated management API. https
    /// needs <see cref="RunnerCertificate"/>; http is allowed only on a loopback address.
    /// </summary>
    public Uri? RunnerUrl { get; init; }

    /// <summary>PFX certificate for an https Runner listener (<c>--runner-cert</c>; password in MYRPA_RUNNER_CERT_PASSWORD).</summary>
    public string? RunnerCertificate { get; init; }

    /// <summary>Lease and heartbeat timing of the Runner protocol.</summary>
    public Action<Orchestration.OrchestratorOptions>? ConfigureOrchestrator { get; init; }

    /// <summary>How often leases are checked and schedules evaluated.</summary>
    public TimeSpan OrchestratorTick { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>A workflow named on the command line: its project and project-relative path (with <c>/</c>).</summary>
internal sealed record OpenWorkflow(string Project, string Path);

/// <summary>
/// <c>MyRPA.Server [--projects-root &lt;dir&gt;] [--project &lt;dir&gt;]... [--open &lt;file&gt;] [--plugin &lt;dir&gt;]... [--plugin-config &lt;file&gt;] [--web &lt;dir&gt;] [--port &lt;n&gt;] [--data &lt;dir&gt;] [--runner-url &lt;url&gt; [--runner-cert &lt;pfx&gt;]]</c>.
/// </summary>
internal static class ServerCommandLine
{
    public const string Usage = "MyRPA.Server [--projects-root <dir>] [--project <dir>]... [--open <workflow.json>] [--plugin <dir>]... [--plugin-config <file>] [--web <dir>] [--port <n>] [--data <dir>] [--runner-url <url> [--runner-cert <pfx>]]";

    /// <summary>Parses the command line.</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="error">The usage error, if any.</param>
    /// <param name="bundledWebRoot">The Web Studio copied next to the server by its build; used when there is no <c>--web</c>.</param>
    /// <param name="defaultProjectsRoot">
    /// The projects folder used when no project is named at all (ADR-0046: <c>Documents/Laconi Projects</c>); created if
    /// missing. Null: a project must be named.
    /// </param>
    /// <param name="defaultDataDirectory">The data folder used without <c>--data</c> (null: no orchestrator).</param>
    public static ServerOptions? Parse(IReadOnlyList<string> args, out string? error, string? bundledWebRoot = null, string? defaultProjectsRoot = null, string? defaultDataDirectory = null)
    {
        string? data = null;
        Uri? runnerUrl = null;
        string? runnerCertificate = null;
        var projects = new List<ProjectRoot>();
        string? projectsRoot = null;
        var plugins = new List<string>();
        string? config = null;
        string? web = null;
        string? open = null;
        var port = 5310;
        var recorderHeadless = false;
        int? recorderDebuggingPort = null;
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (name == "--recorder-headless")
            {
                // Test-only (ADR-0039): recording browsers without a window, for CI which has no display.
                recorderHeadless = true;
                continue;
            }

            if (name is not ("--project" or "--projects-root" or "--open" or "--plugin" or "--plugin-config" or "--web" or "--port" or "--recorder-debugging-port" or "--data" or "--runner-url" or "--runner-cert"))
            {
                error = $"Unexpected argument '{name}'.";
                return null;
            }

            if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
            {
                error = $"{name} needs a value.";
                return null;
            }

            var value = args[++i];
            switch (name)
            {
                case "--project":
                    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
                    if (!Directory.Exists(root))
                    {
                        error = $"Project folder '{root}' does not exist.";
                        return null;
                    }

                    var projectName = Path.GetFileName(root);
                    if (string.IsNullOrEmpty(projectName) || projects.Any(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase)))
                    {
                        error = $"Project folder '{root}' has no usable name or its name is used twice.";
                        return null;
                    }

                    projects.Add(new ProjectRoot(projectName, root));
                    break;
                case "--projects-root" when projectsRoot is null:
                    if (!TryProjectsRoot(value, out projectsRoot, out error))
                    {
                        return null;
                    }

                    break;
                case "--projects-root":
                    error = "--projects-root may be given once.";
                    return null;
                case "--open" when open is null:
                    open = Path.GetFullPath(value);
                    if (!File.Exists(open) || !open.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        error = $"Workflow file '{open}' does not exist or is not a .json file.";
                        return null;
                    }

                    break;
                case "--open":
                    error = "--open may be given once.";
                    return null;
                case "--plugin":
                    plugins.Add(value);
                    break;
                case "--plugin-config" when config is null:
                    config = value;
                    break;
                case "--plugin-config":
                    error = "--plugin-config may be given once.";
                    return null;
                case "--web" when web is null:
                    web = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
                    if (!File.Exists(Path.Combine(web, "index.html")))
                    {
                        error = $"Web Studio folder '{web}' has no index.html (build it with 'npm run build' in web/studio).";
                        return null;
                    }

                    break;
                case "--web":
                    error = "--web may be given once.";
                    return null;
                case "--port" when int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var p) && p is >= 0 and <= 65535:
                    port = p;
                    break;
                case "--recorder-debugging-port" when int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var d) && d is > 0 and <= 65535:
                    // Test-only (ADR-0039): an end-to-end test acts as the user in the recording browser over DevTools.
                    recorderDebuggingPort = d;
                    break;
                case "--recorder-debugging-port":
                    error = "--recorder-debugging-port must be a number from 1 to 65535.";
                    return null;
                case "--data" when data is null:
                    data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
                    break;
                case "--runner-url" when runnerUrl is null:
                    if (!TryRunnerUrl(value, out runnerUrl, out error))
                    {
                        return null;
                    }

                    break;
                case "--runner-cert" when runnerCertificate is null:
                    runnerCertificate = Path.GetFullPath(value);
                    if (!File.Exists(runnerCertificate))
                    {
                        error = $"Certificate file '{runnerCertificate}' does not exist.";
                        return null;
                    }

                    break;
                case "--data" or "--runner-url" or "--runner-cert":
                    error = $"{name} may be given once.";
                    return null;
                default:
                    error = $"--port must be a number from 0 to 65535.";
                    return null;
            }
        }

        // Opening a file alone is enough: its folder becomes the project (one command, like the WPF Studio's argument).
        if (projects.Count == 0 && open is not null)
        {
            var folder = Path.GetDirectoryName(open)!;
            if (!string.IsNullOrEmpty(Path.GetFileName(folder)))
            {
                projects.Add(new ProjectRoot(Path.GetFileName(folder), folder));
            }
        }

        // Nothing named at all: the default projects folder, so 'MyRPA.Server' alone starts a usable Studio.
        if (projects.Count == 0 && projectsRoot is null && open is null && defaultProjectsRoot is not null && !TryProjectsRoot(defaultProjectsRoot, out projectsRoot, out error))
        {
            return null;
        }

        if (projects.Count == 0 && projectsRoot is null)
        {
            error = "At least one --project folder, a --projects-root folder or an --open workflow is required.";
            return null;
        }

        OpenWorkflow? openWorkflow = null;
        if (open is not null)
        {
            var project = projects.FirstOrDefault(p => open.StartsWith(p.Root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (project is null)
            {
                error = $"Workflow file '{open}' is not inside a --project folder.";
                return null;
            }

            openWorkflow = new OpenWorkflow(project.Name, Path.GetRelativePath(project.Root, open).Replace('\\', '/'));
        }

        web ??= bundledWebRoot is not null && File.Exists(Path.Combine(bundledWebRoot, "index.html")) ? bundledWebRoot : null;
        data ??= defaultDataDirectory;
        if (runnerUrl is not null && data is null)
        {
            error = "--runner-url needs the orchestrator, which needs a --data folder.";
            return null;
        }

        if (runnerUrl is { Scheme: "https" } && runnerCertificate is null)
        {
            error = "An https --runner-url needs --runner-cert <pfx> (password in the MYRPA_RUNNER_CERT_PASSWORD environment variable).";
            return null;
        }

        if (runnerCertificate is not null && runnerUrl is not { Scheme: "https" })
        {
            error = "--runner-cert is used only with an https --runner-url.";
            return null;
        }

        error = null;
        return new ServerOptions
        {
            Projects = projects,
            ProjectsRoot = projectsRoot,
            PluginDirectories = plugins,
            PluginConfiguration = config,
            WebRoot = web,
            Port = port,
            Open = openWorkflow,
            RecorderHeadless = recorderHeadless,
            RecorderDebuggingPort = recorderDebuggingPort,
            DataDirectory = data,
            RunnerUrl = runnerUrl,
            RunnerCertificate = runnerCertificate,
        };
    }

    /// <summary>
    /// A Runner listener address (ADR-0051): http or https with an IP address (or localhost) and a port. Plain http is
    /// refused anywhere but loopback, so Runner tokens never cross a network unencrypted.
    /// </summary>
    internal static bool TryRunnerUrl(string value, out Uri? url, out string? error)
    {
        url = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https") || parsed.AbsolutePath != "/" || parsed.Query.Length > 0)
        {
            error = $"--runner-url '{value}' must look like https://0.0.0.0:5311 (scheme, address and port only).";
            return false;
        }

        var host = parsed.Host.Trim('[', ']');
        var loopback = host is "localhost" || (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip));
        if (!loopback && !System.Net.IPAddress.TryParse(host, out _))
        {
            error = $"--runner-url '{value}' must use an IP address (0.0.0.0 for every interface) or localhost.";
            return false;
        }

        if (parsed.Scheme == "http" && !loopback)
        {
            error = "A Runner listener on the network must use https; plain http is allowed only on a loopback address.";
            return false;
        }

        url = parsed;
        error = null;
        return true;
    }

    /// <summary>The projects folder as a full path, created if missing (ADR-0046).</summary>
    private static bool TryProjectsRoot(string value, out string? root, out string? error)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        try
        {
            Directory.CreateDirectory(root);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Projects folder '{root}' cannot be created: {ex.Message}";
            root = null;
            return false;
        }
    }
}
