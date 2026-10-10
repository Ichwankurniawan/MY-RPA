using System.Globalization;
using MyRPA.Sdk.Files;
using MyRPA.Sdk.Plugins;

namespace MyRPA.Scripting;

/// <summary>Plugin settings, built once in <see cref="ScriptingPlugin.Initialize"/>.</summary>
/// <param name="MaxTimeoutMs">The longest timeout a script may ask for.</param>
/// <param name="MaxStatements">The most JavaScript statements one script runs.</param>
/// <param name="MaxMemoryBytes">The most memory one JavaScript run may allocate.</param>
/// <param name="MaxRecursion">The deepest JavaScript call depth.</param>
/// <param name="MaxResultBytes">The largest result (as JSON) and inputs a script exchanges.</param>
/// <param name="PythonPath">The Python interpreter; null when Python is not enabled.</param>
/// <param name="Files">The working folder of Python scripts (the plugin's fileRoot), or null for a private temporary folder.</param>
/// <param name="MaxOutputBytes">The most standard output and error kept from a Python run.</param>
public sealed record ScriptingOptions(
    int MaxTimeoutMs, int MaxStatements, long MaxMemoryBytes, int MaxRecursion, long MaxResultBytes, string? PythonPath, FileRootPolicy? Files, int MaxOutputBytes);

/// <summary>
/// Entry point of the scripting plugin (ADR-0045): sandboxed JavaScript always, Python only when the operator sets
/// <c>pythonPath</c>. Settings: <c>maxTimeoutMs</c> (300000), <c>maxStatements</c> (10 million), <c>maxMemoryBytes</c>
/// (256 MB), <c>maxRecursion</c> (500), <c>maxResultBytes</c> (10 MB), <c>pythonPath</c>, <c>fileRoot</c>,
/// <c>maxOutputBytes</c> (1 MB).
/// </summary>
public sealed class ScriptingPlugin : IPlugin
{
    private ScriptingOptions? _options;

    /// <inheritdoc />
    public void Initialize(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings;
        string? Text(string name) => settings.TryGetValue(name, out var value) && value.Trim().Length > 0 ? value.Trim() : null;
        var python = Text("pythonPath");
        if (python is not null && !File.Exists(python))
        {
            throw new ArgumentException($"Setting 'pythonPath' must name the Python interpreter file (such as C:\\Python312\\python.exe or /usr/bin/python3); '{python}' does not exist.", nameof(context));
        }

        _options = new ScriptingOptions(
            (int)Number(settings, "maxTimeoutMs", 300_000, 1, 3_600_000),
            (int)Number(settings, "maxStatements", 10_000_000, 1, int.MaxValue),
            Number(settings, "maxMemoryBytes", 256L * 1024 * 1024, 1024 * 1024, long.MaxValue),
            (int)Number(settings, "maxRecursion", 500, 10, 100_000),
            Number(settings, "maxResultBytes", 10L * 1024 * 1024, 1024, int.MaxValue),
            python,
            Text("fileRoot") is { } root ? new FileRootPolicy(root) : null,
            (int)Number(settings, "maxOutputBytes", 1024 * 1024, 1024, int.MaxValue));
    }

    /// <inheritdoc />
    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        registrar
            .AddInstance(_options ?? throw new InvalidOperationException("Initialize must run before Register."))
            .AddActivity<JavaScriptActivity>(JavaScriptActivity.Descriptor)
            .AddActivity<PythonActivity>(PythonActivity.Descriptor);
    }

    private static long Number(IReadOnlyDictionary<string, string> settings, string name, long defaultValue, long min, long max)
    {
        if (!settings.TryGetValue(name, out var text))
        {
            return defaultValue;
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : throw new ArgumentException($"Setting '{name}' must be a whole number from {min} to {max}, not '{text}'.", nameof(settings));
    }
}
