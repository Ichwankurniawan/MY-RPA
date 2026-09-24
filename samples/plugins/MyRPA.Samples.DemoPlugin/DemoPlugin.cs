using MyRPA.Sdk.Plugins;

namespace MyRPA.Samples.DemoPlugin;

/// <summary>
/// Entry point of the sample plugin, named by <c>entryPoint.type</c> in <c>myrpa-plugin.json</c>. It shows the whole
/// plugin contract without touching a real technology: settings in <see cref="Initialize"/>, and explicit registration
/// of exactly the activities and provider declared in the manifest.
/// </summary>
public sealed class DemoPlugin : IPlugin
{
    /// <summary>Setting that prefixes every <c>Demo.Echo</c> result.</summary>
    public const string EchoPrefixSetting = "echoPrefix";

    private DemoOptions? _options;

    /// <inheritdoc />
    public void Initialize(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var prefix = context.Settings.TryGetValue(EchoPrefixSetting, out var value) ? value : string.Empty;
        if (prefix.Length > 32)
        {
            throw new ArgumentException($"Setting '{EchoPrefixSetting}' must be at most 32 characters.", nameof(context));
        }

        _options = new DemoOptions(prefix);
    }

    /// <inheritdoc />
    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        registrar
            .AddInstance(_options ?? throw new InvalidOperationException("Initialize must run before Register."))
            .AddProvider<IDemoTextProvider, DemoTextProvider>(DemoTextProvider.Id)
            .AddActivity<EchoActivity>(EchoActivity.Descriptor)
            .AddActivity<GetFieldActivity>(GetFieldActivity.Descriptor);
    }
}

/// <summary>Settings of the sample plugin, built once from <see cref="PluginContext.Settings"/>.</summary>
/// <param name="EchoPrefix">Text prepended to every <c>Demo.Echo</c> result.</param>
public sealed record DemoOptions(string EchoPrefix);
