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

    /// <summary>Loopback port; 0 picks a free one.</summary>
    public int Port { get; init; } = 5310;

    /// <summary>Largest accepted request body (documents are at most a few hundred KB; ADR-0012 caps files at 5 MB).</summary>
    public long MaxRequestBodyBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>How long the one-time start token is valid.</summary>
    public TimeSpan StartTokenLifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Open event streams (one per browser tab) the server keeps.</summary>
    public int MaxStreams { get; init; } = 32;

    /// <summary>Runs one stream may subscribe to.</summary>
    public int MaxSubscriptionsPerStream { get; init; } = 64;

    /// <summary>Events queued for one stream connection; a slower client is disconnected and resumes by cursor.</summary>
    public int StreamQueueCapacity { get; init; } = 4096;

    /// <summary>How long a disconnected stream is kept for reconnection.</summary>
    public TimeSpan StreamRetention { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Keep-alive comment interval on idle streams.</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Execution limits (concurrency, replay buffer, retention).</summary>
    public Action<Execution.Hosting.ExecutionHostOptions>? ConfigureHosting { get; init; }
}

/// <summary>
/// <c>MyRPA.Server --project &lt;dir&gt; [--project &lt;dir&gt;]... [--plugin &lt;dir&gt;]... [--plugin-config &lt;file&gt;] [--port &lt;n&gt;]</c>.
/// </summary>
internal static class ServerCommandLine
{
    public const string Usage = "MyRPA.Server --project <dir> [--project <dir>]... [--plugin <dir>]... [--plugin-config <file>] [--port <n>]";

    public static ServerOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        var projects = new List<ProjectRoot>();
        var plugins = new List<string>();
        string? config = null;
        var port = 5310;
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (name is not ("--project" or "--plugin" or "--plugin-config" or "--port"))
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
                case "--plugin":
                    plugins.Add(value);
                    break;
                case "--plugin-config" when config is null:
                    config = value;
                    break;
                case "--plugin-config":
                    error = "--plugin-config may be given once.";
                    return null;
                case "--port" when int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var p) && p is >= 0 and <= 65535:
                    port = p;
                    break;
                default:
                    error = $"--port must be a number from 0 to 65535.";
                    return null;
            }
        }

        if (projects.Count == 0)
        {
            error = "At least one --project folder is required.";
            return null;
        }

        error = null;
        return new ServerOptions { Projects = projects, PluginDirectories = plugins, PluginConfiguration = config, Port = port };
    }
}
