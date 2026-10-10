using System.Globalization;
using MyRPA.Sdk.Files;
using MyRPA.Sdk.Plugins;

namespace MyRPA.Documents;

/// <summary>
/// Entry point of the documents plugin (Phase 7.1, ADR-0043). Settings:
/// <list type="bullet">
/// <item><c>fileRoot</c> — the only folder tree the activities may read (default: the host's working directory).</item>
/// <item><c>maxFileBytes</c> — the largest PDF read (default 50 MB).</item>
/// <item><c>maxPages</c> — the most pages one activity reads (default 2000).</item>
/// <item><c>maxTextChars</c> — the most characters of text one activity returns (default 10 million).</item>
/// </list>
/// </summary>
public sealed class DocumentsPlugin : IPlugin
{
    private DocumentsOptions? _options;

    /// <inheritdoc />
    public void Initialize(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings;
        var root = settings.TryGetValue("fileRoot", out var rootText) ? rootText : Environment.CurrentDirectory;
        _options = new DocumentsOptions(
            new FileRootPolicy(root),
            Positive(settings, "maxFileBytes", 50L * 1024 * 1024),
            (int)Positive(settings, "maxPages", 2000),
            Positive(settings, "maxTextChars", 10_000_000));
    }

    /// <inheritdoc />
    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        registrar
            .AddInstance(_options ?? throw new InvalidOperationException("Initialize must run before Register."))
            .AddActivity<PdfReadTextActivity>(PdfReadTextActivity.Descriptor)
            .AddActivity<PdfGetInfoActivity>(PdfGetInfoActivity.Descriptor);
    }

    private static long Positive(IReadOnlyDictionary<string, string> settings, string name, long defaultValue)
    {
        if (!settings.TryGetValue(name, out var text))
        {
            return defaultValue;
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is > 0 and <= int.MaxValue
            ? value
            : throw new ArgumentException($"Setting '{name}' must be a positive whole number, not '{text}'.", nameof(settings));
    }
}

/// <summary>Plugin settings, built once in <see cref="DocumentsPlugin.Initialize"/>.</summary>
/// <param name="Files">Where documents may be read.</param>
/// <param name="MaxFileBytes">The largest PDF read.</param>
/// <param name="MaxPages">The most pages one activity reads.</param>
/// <param name="MaxTextChars">The most characters of text one activity returns.</param>
public sealed record DocumentsOptions(FileRootPolicy Files, long MaxFileBytes, int MaxPages, long MaxTextChars);
