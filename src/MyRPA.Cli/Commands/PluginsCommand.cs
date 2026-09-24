using MyRPA.Plugins;
using MyRPA.Sdk;

namespace MyRPA.Cli.Commands;

/// <summary>
/// <c>myrpa plugins</c>: lists the plugins loaded with <c>--plugin</c>, including the SHA-256 digest to pin in a host
/// allow-list. Load diagnostics are written to standard error before any command runs.
/// </summary>
/// <param name="output">CLI output writers.</param>
/// <param name="registry">Loaded plugins.</param>
public sealed class PluginsCommand(CliOutput output, IPluginRegistry registry) : ICliCommand
{
    /// <inheritdoc />
    public string Name => "plugins";

    /// <inheritdoc />
    public string Usage => "--plugin <directory> plugins";

    /// <inheritdoc />
    public string Description => "List loaded plugins with their activities, providers and SHA-256 digest.";

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        var writer = output.Out;

        await writer.WriteLineAsync($"Automation SDK: {AutomationSdk.Version}").ConfigureAwait(false);
        await writer.WriteLineAsync($"Plugins: {registry.Plugins.Count}").ConfigureAwait(false);
        foreach (var plugin in registry.Plugins)
        {
            var manifest = plugin.Manifest;
            await writer.WriteLineAsync($"  {manifest.Id} {manifest.Version} - {manifest.Name}").ConfigureAwait(false);
            await writer.WriteLineAsync($"    Directory:    {plugin.Directory}").ConfigureAwait(false);
            await writer.WriteLineAsync($"    SHA-256:      {plugin.Digest}").ConfigureAwait(false);
            await writer.WriteLineAsync($"    SDK:          {manifest.SdkVersion} ({manifest.TargetFramework})").ConfigureAwait(false);
            await writer.WriteLineAsync($"    Capabilities: {List(manifest.Capabilities)}").ConfigureAwait(false);
            await writer.WriteLineAsync($"    Activities:   {List(manifest.Activities.Select(a => a.Value))}").ConfigureAwait(false);
            await writer.WriteLineAsync($"    Providers:    {List(manifest.Providers.Select(p => p.Value))}").ConfigureAwait(false);
        }

        return CliExitCodes.Success;
    }

    private static string List(IEnumerable<string> items)
    {
        var text = string.Join(", ", items);
        return text.Length == 0 ? "(none)" : text;
    }
}

/// <summary>The registry when no plugins are loaded.</summary>
internal sealed class NoPlugins : IPluginRegistry
{
    public IReadOnlyList<LoadedPlugin> Plugins { get; } = [];

    public IReadOnlyList<PluginDiagnostic> Diagnostics { get; } = [];
}
