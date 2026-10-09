using System.Globalization;
using MyRPA.Sdk.Files;
using MyRPA.Sdk.Plugins;

namespace MyRPA.Files;

/// <summary>
/// Entry point of the files plugin (Phase 7, ADR-0042). Settings:
/// <list type="bullet">
/// <item><c>fileRoot</c> — the only folder tree the activities may use (default: the host's working directory).</item>
/// <item><c>maxFileBytes</c> — the largest file read (default 50 MB).</item>
/// <item><c>maxItems</c> — the most entries File.List returns (default 10000).</item>
/// <item><c>maxRows</c> — the most rows Csv.Read returns (default 100000).</item>
/// </list>
/// </summary>
public sealed class FilesPlugin : IPlugin
{
    private FilesOptions? _options;

    /// <inheritdoc />
    public void Initialize(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings;
        var root = settings.TryGetValue("fileRoot", out var rootText) ? rootText : Environment.CurrentDirectory;
        _options = new FilesOptions(
            new FileRootPolicy(root),
            Positive(settings, "maxFileBytes", 50L * 1024 * 1024),
            (int)Positive(settings, "maxItems", 10_000),
            (int)Positive(settings, "maxRows", 100_000));
    }

    /// <inheritdoc />
    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        registrar
            .AddInstance(_options ?? throw new InvalidOperationException("Initialize must run before Register."))
            .AddActivity<FileExistsActivity>(FileExistsActivity.Descriptor)
            .AddActivity<FileListActivity>(FileListActivity.Descriptor)
            .AddActivity<FileReadTextActivity>(FileReadTextActivity.Descriptor)
            .AddActivity<FileWriteTextActivity>(FileWriteTextActivity.Descriptor)
            .AddActivity<FileCopyActivity>(FileCopyActivity.Descriptor)
            .AddActivity<FileMoveActivity>(FileMoveActivity.Descriptor)
            .AddActivity<FileDeleteActivity>(FileDeleteActivity.Descriptor)
            .AddActivity<FolderCreateActivity>(FolderCreateActivity.Descriptor)
            .AddActivity<CsvReadActivity>(CsvReadActivity.Descriptor)
            .AddActivity<CsvWriteActivity>(CsvWriteActivity.Descriptor)
            .AddActivity<JsonReadFileActivity>(JsonReadFileActivity.Descriptor)
            .AddActivity<JsonWriteFileActivity>(JsonWriteFileActivity.Descriptor)
            .AddActivity<XmlReadFileActivity>(XmlReadFileActivity.Descriptor);
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

/// <summary>Plugin settings, built once in <see cref="FilesPlugin.Initialize"/>.</summary>
/// <param name="Files">Where files may be read and written.</param>
/// <param name="MaxFileBytes">The largest file read.</param>
/// <param name="MaxItems">The most entries File.List returns.</param>
/// <param name="MaxRows">The most rows Csv.Read returns.</param>
public sealed record FilesOptions(FileRootPolicy Files, long MaxFileBytes, int MaxItems, int MaxRows);
