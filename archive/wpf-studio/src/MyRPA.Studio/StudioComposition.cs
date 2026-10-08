using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MyRPA.Activities;
using MyRPA.Plugins;
using MyRPA.Runtime;
using MyRPA.Storage;
using MyRPA.Studio.Running;
using MyRPA.Studio.Services;
using MyRPA.Studio.ViewModels;

namespace MyRPA.Studio;

/// <summary>Studio's command line: <c>MyRPA.Studio [workflow.json] [--plugin &lt;dir&gt;]... [--plugin-config &lt;file&gt;]</c>.</summary>
/// <param name="WorkflowFile">A workflow to open.</param>
/// <param name="PluginDirectories">Plugins to load (required).</param>
/// <param name="PluginConfiguration">A plugin configuration file (ADR-0019).</param>
internal sealed record StudioCommandLine(string? WorkflowFile, IReadOnlyList<string> PluginDirectories, string? PluginConfiguration)
{
    /// <summary>Usage text.</summary>
    public const string Usage = "MyRPA.Studio [workflow.json] [--plugin <directory>]... [--plugin-config <file>]";

    /// <summary>Parses the command line.</summary>
    /// <param name="args">Arguments.</param>
    /// <param name="error">Why parsing failed.</param>
    public static StudioCommandLine? Parse(IReadOnlyList<string> args, out string? error)
    {
        string? file = null;
        string? config = null;
        var directories = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "--plugin" or "--plugin-config")
            {
                if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || (arg == "--plugin-config" && config is not null))
                {
                    error = arg == "--plugin" ? "--plugin needs a plugin directory." : "--plugin-config needs one plugin configuration file (given once).";
                    return null;
                }

                if (arg == "--plugin")
                {
                    directories.Add(args[++i]);
                }
                else
                {
                    config = args[++i];
                }
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal) || file is not null)
            {
                error = $"Unexpected argument '{arg}'.";
                return null;
            }
            else
            {
                file = arg;
            }
        }

        error = null;
        return new StudioCommandLine(file, directories, config);
    }
}

/// <summary>Composes Studio: the same engine, activities, storage and plugin host as the CLI, plus the Studio services.</summary>
internal static class StudioComposition
{
    /// <summary>Host application name.</summary>
    public const string ApplicationName = "MyRPA.Studio";

    /// <summary>Loads the plugins named on the command line; null when none are named.</summary>
    /// <param name="commandLine">The command line.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="PluginConfigurationException">The configuration file is invalid.</exception>
    public static async Task<PluginSet?> LoadPluginsAsync(StudioCommandLine commandLine, CancellationToken cancellationToken)
    {
        if (commandLine.PluginDirectories.Count == 0 && commandLine.PluginConfiguration is null)
        {
            return null;
        }

        var options = await PluginConfigurationFile.CreateHostOptionsAsync(commandLine.PluginDirectories, commandLine.PluginConfiguration, cancellationToken).ConfigureAwait(true);
        return await PluginLoader.LoadAsync(options, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Builds the host.</summary>
    /// <param name="plugins">Loaded plugins; the caller disposes them after the host.</param>
    /// <param name="configure">Replaces services (tests use it for dialogs).</param>
    public static IHost BuildHost(PluginSet? plugins, Action<IServiceCollection>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = ApplicationName,
            // Configuration files are read from the install directory, never from the working directory.
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true,
        });

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Services.AddSingleton<StudioLogFeed>();
        builder.Services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<StudioLogFeed>());

        builder.Services.AddMyRpaRuntime();
        builder.Services.AddMyRpaActivities();
        builder.Services.AddMyRpaStorage();
        if (plugins is not null)
        {
            builder.Services.AddMyRpaPlugins(plugins);
        }

        builder.Services.AddSingleton<IUiDispatcher, WpfDispatcher>();
        builder.Services.AddSingleton<IStudioDialogs, WpfDialogs>();
        builder.Services.AddSingleton<IStudioClipboard, WpfClipboard>();
        builder.Services.AddSingleton<IWorkflowStorage, FileWorkflowStorage>();
        builder.Services.AddSingleton<StudioViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        configure?.Invoke(builder.Services);

        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        }));

        return builder.Build();
    }
}
