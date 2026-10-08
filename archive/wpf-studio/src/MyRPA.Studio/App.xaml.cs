using System.Text;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyRPA.Plugins;
using MyRPA.Studio.ViewModels;

namespace MyRPA.Studio;

/// <summary>
/// The Studio application: loads plugins (as the CLI does), builds the host, shows the window and, when the window has
/// closed, stops the host and unloads plugins. Startup and shutdown are asynchronous and never block the UI thread.
/// </summary>
internal sealed partial class App : Application
{
    /// <summary>Exit codes, aligned with the CLI's.</summary>
    private const int ExitFailure = 1;
    private const int ExitUsage = 2;
    private const int ExitPluginFailure = 5;

    private IHost? _host;
    private PluginSet? _plugins;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);
        _ = StartAsync(e.Args);
    }

    private async Task StartAsync(string[] args)
    {
        try
        {
            if (StudioCommandLine.Parse(args, out var usageError) is not { } commandLine)
            {
                Fail($"{usageError}\n\nUsage: {StudioCommandLine.Usage}");
                await ShutdownAsync(ExitUsage).ConfigureAwait(true);
                return;
            }

            try
            {
                _plugins = await StudioComposition.LoadPluginsAsync(commandLine, CancellationToken.None).ConfigureAwait(true);
            }
            catch (PluginConfigurationException ex)
            {
                Fail(ex.Message);
                await ShutdownAsync(ExitPluginFailure).ConfigureAwait(true);
                return;
            }

            if (_plugins is { HasRequiredFailures: true })
            {
                var report = new StringBuilder("Plugins failed to load; Studio was not started.\n\n");
                foreach (var diagnostic in _plugins.Diagnostics)
                {
                    report.AppendLine(diagnostic.ToString());
                }

                Fail(report.ToString());
                await ShutdownAsync(ExitPluginFailure).ConfigureAwait(true);
                return;
            }

            _host = StudioComposition.BuildHost(_plugins);
            await _host.StartAsync(CancellationToken.None).ConfigureAwait(true);
            _plugins?.VerifyProviders(_host.Services);

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Closed += OnMainWindowClosed;
            window.Show();

            if (commandLine.WorkflowFile is { } file)
            {
                await _host.Services.GetRequiredService<StudioViewModel>().OpenFileAsync(file, CancellationToken.None).ConfigureAwait(true);
            }
        }
#pragma warning disable CA1031 // Top-level boundary: report any startup failure to the user instead of crashing silently.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Fail($"MyRPA Studio could not start.\n\n{ex.Message}");
            await ShutdownAsync(ExitFailure).ConfigureAwait(true);
        }
    }

    private void OnMainWindowClosed(object? sender, EventArgs e) => _ = ShutdownAsync(0);

    private async Task ShutdownAsync(int exitCode)
    {
        try
        {
            if (_host is not null)
            {
                await _host.StopAsync(CancellationToken.None).ConfigureAwait(true);
                _host.Dispose();
                _host = null;
            }

            // After the host (and every plugin service): dispose plugins and unload them.
            if (_plugins is not null)
            {
                await _plugins.DisposeAsync().ConfigureAwait(true);
                _plugins = null;
            }
        }
        finally
        {
            Shutdown(exitCode);
        }
    }

    private static void Fail(string message) => MessageBox.Show(message, "MyRPA Studio", MessageBoxButton.OK, MessageBoxImage.Error);
}
