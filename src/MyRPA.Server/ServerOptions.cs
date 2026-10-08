namespace MyRPA.Server;

/// <summary>A registered project: a folder whose workflow files the server exposes (ADR-0025).</summary>
/// <param name="Name">Project name (the folder name).</param>
/// <param name="Root">Full path of the folder.</param>
internal sealed record ProjectRoot(string Name, string Root);

/// <summary>Server configuration, from the command line (local mode).</summary>
internal sealed record ServerOptions
{
    /// <summary>Registered projects.</summary>
    public IReadOnlyList<ProjectRoot> Projects { get; init; } = [];

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
}

/// <summary>A workflow named on the command line: its project and project-relative path (with <c>/</c>).</summary>
internal sealed record OpenWorkflow(string Project, string Path);

/// <summary>
/// <c>MyRPA.Server --project &lt;dir&gt;... [--open &lt;file&gt;] [--plugin &lt;dir&gt;]... [--plugin-config &lt;file&gt;] [--web &lt;dir&gt;] [--port &lt;n&gt;]</c>.
/// </summary>
internal static class ServerCommandLine
{
    public const string Usage = "MyRPA.Server --project <dir> [--project <dir>]... [--open <workflow.json>] [--plugin <dir>]... [--plugin-config <file>] [--web <dir>] [--port <n>]";

    /// <summary>Parses the command line.</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="error">The usage error, if any.</param>
    /// <param name="bundledWebRoot">The Web Studio copied next to the server by its build; used when there is no <c>--web</c>.</param>
    public static ServerOptions? Parse(IReadOnlyList<string> args, out string? error, string? bundledWebRoot = null)
    {
        var projects = new List<ProjectRoot>();
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

            if (name is not ("--project" or "--open" or "--plugin" or "--plugin-config" or "--web" or "--port" or "--recorder-debugging-port"))
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

        if (projects.Count == 0)
        {
            error = "At least one --project folder (or an --open workflow) is required.";
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
        error = null;
        return new ServerOptions { Projects = projects, PluginDirectories = plugins, PluginConfiguration = config, WebRoot = web, Port = port, Open = openWorkflow, RecorderHeadless = recorderHeadless, RecorderDebuggingPort = recorderDebuggingPort };
    }
}
