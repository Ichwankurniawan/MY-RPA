using System.Text.Json;

namespace MyRPA.Plugins;

/// <summary>
/// Reads a host's plugin configuration file (ADR-0019): the operator's allow-list of plugin directories with their
/// pinned SHA-256 digests, whether each is required, their settings, the capabilities the host denies and whether
/// every plugin must be pinned. The same file is used by the CLI (<c>--plugin-config</c>) and Studio.
/// </summary>
/// <remarks>
/// Format (unknown properties are errors, because a misspelt security option must not be silently ignored):
/// <code>
/// {
///   "pluginConfigVersion": "1.0",
///   "requireIntegrity": true,
///   "deniedCapabilities": [ "filesystem.write" ],
///   "plugins": [
///     { "directory": "plugins/browser", "sha256": "…", "required": true, "settings": { "headless": "true" } }
///   ]
/// }
/// </code>
/// Relative directories are resolved against the configuration file's directory, never the working directory.
/// </remarks>
public static class PluginConfigurationFile
{
    /// <summary>The supported <c>pluginConfigVersion</c>.</summary>
    public const string CurrentVersion = "1.0";

    private static readonly JsonDocumentOptions _documentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Reads a configuration file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The host options it describes.</returns>
    /// <exception cref="PluginConfigurationException">The file cannot be read or is invalid.</exception>
    public static async Task<PluginHostOptions> LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        string json;
        try
        {
            json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginConfigurationException($"Plugin configuration '{fullPath}' cannot be read: {ex.Message}", ex);
        }

        return Parse(json, Path.GetDirectoryName(fullPath)!, fullPath);
    }

    /// <summary>
    /// Builds a host's plugin options from its command line: the configuration file (if any) plus plugin directories
    /// named directly, which are required and unpinned. A directory already in the file keeps the file's settings.
    /// </summary>
    /// <param name="directories">Plugin directories named on the command line.</param>
    /// <param name="configurationPath">The configuration file, or null.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="PluginConfigurationException">The file cannot be read or is invalid.</exception>
    public static async Task<PluginHostOptions> CreateHostOptionsAsync(IEnumerable<string> directories, string? configurationPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directories);
        var options = configurationPath is null
            ? new PluginHostOptions()
            : await LoadAsync(configurationPath, cancellationToken).ConfigureAwait(false);
        foreach (var directory in directories.Select(Path.GetFullPath))
        {
            if (!options.Sources.Any(s => string.Equals(s.Directory, directory, StringComparison.OrdinalIgnoreCase)))
            {
                options.Sources.Add(new PluginSource { Directory = directory, Required = true });
            }
        }

        return options;
    }

    /// <summary>Parses configuration JSON.</summary>
    /// <param name="json">The JSON.</param>
    /// <param name="baseDirectory">Directory that relative plugin directories are resolved against.</param>
    /// <param name="sourceName">Name used in error messages.</param>
    /// <returns>The host options.</returns>
    /// <exception cref="PluginConfigurationException">The JSON is invalid.</exception>
    public static PluginHostOptions Parse(string json, string baseDirectory, string sourceName = "plugin configuration")
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        try
        {
            using var document = JsonDocument.Parse(json, _documentOptions);
            return Read(document.RootElement, baseDirectory);
        }
        catch (JsonException ex)
        {
            throw new PluginConfigurationException($"{sourceName}: invalid JSON: {ex.Message}", ex);
        }
        catch (FormatException ex)
        {
            throw new PluginConfigurationException($"{sourceName}: {ex.Message}", ex);
        }
    }

    private static PluginHostOptions Read(JsonElement root, string baseDirectory)
    {
        RequireKind(root, JsonValueKind.Object, "$");
        CheckProperties(root, "$", "pluginConfigVersion", "requireIntegrity", "deniedCapabilities", "plugins");
        var version = root.TryGetProperty("pluginConfigVersion", out var v) ? String(v, "$.pluginConfigVersion") : null;
        if (version != CurrentVersion)
        {
            throw new FormatException($"$.pluginConfigVersion must be \"{CurrentVersion}\".");
        }

        var options = new PluginHostOptions();
        if (root.TryGetProperty("requireIntegrity", out var integrity))
        {
            options.RequireIntegrity = Boolean(integrity, "$.requireIntegrity");
        }

        if (root.TryGetProperty("deniedCapabilities", out var denied))
        {
            RequireKind(denied, JsonValueKind.Array, "$.deniedCapabilities");
            var i = 0;
            foreach (var item in denied.EnumerateArray())
            {
                options.DeniedCapabilities.Add(NonEmpty(item, $"$.deniedCapabilities[{i++}]"));
            }
        }

        if (root.TryGetProperty("plugins", out var plugins))
        {
            RequireKind(plugins, JsonValueKind.Array, "$.plugins");
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var i = 0;
            foreach (var item in plugins.EnumerateArray())
            {
                var source = ReadSource(item, $"$.plugins[{i++}]", baseDirectory);
                if (!directories.Add(source.Directory))
                {
                    throw new FormatException($"Plugin directory '{source.Directory}' is listed more than once.");
                }

                options.Sources.Add(source);
            }
        }

        return options;
    }

    private static PluginSource ReadSource(JsonElement element, string path, string baseDirectory)
    {
        RequireKind(element, JsonValueKind.Object, path);
        CheckProperties(element, path, "directory", "sha256", "required", "settings");
        if (!element.TryGetProperty("directory", out var directory))
        {
            throw new FormatException($"{path}.directory is required.");
        }

        var source = new PluginSource { Directory = Path.GetFullPath(Path.Combine(baseDirectory, NonEmpty(directory, $"{path}.directory"))) };
        if (element.TryGetProperty("sha256", out var sha))
        {
            var digest = NonEmpty(sha, $"{path}.sha256");
            if (digest.Length != 64 || !digest.All(Uri.IsHexDigit))
            {
                throw new FormatException($"{path}.sha256 must be 64 hexadecimal characters.");
            }

            source.Sha256 = digest.ToLowerInvariant();
        }

        if (element.TryGetProperty("required", out var required))
        {
            source.Required = Boolean(required, $"{path}.required");
        }

        if (element.TryGetProperty("settings", out var settings))
        {
            RequireKind(settings, JsonValueKind.Object, $"{path}.settings");
            foreach (var setting in settings.EnumerateObject())
            {
                source.Settings[setting.Name] = String(setting.Value, $"{path}.settings.{setting.Name}");
            }
        }

        return source;
    }

    private static void CheckProperties(JsonElement element, string path, params string[] known)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!known.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new FormatException($"{path}.{property.Name} is not a known property (expected one of: {string.Join(", ", known)}).");
            }
        }
    }

    private static void RequireKind(JsonElement element, JsonValueKind kind, string path)
    {
        if (element.ValueKind != kind)
        {
            throw new FormatException($"{path} must be a JSON {kind.ToString().ToLowerInvariant()}.");
        }
    }

    private static string String(JsonElement element, string path)
    {
        RequireKind(element, JsonValueKind.String, path);
        return element.GetString()!;
    }

    private static string NonEmpty(JsonElement element, string path)
    {
        var value = String(element, path);
        return string.IsNullOrWhiteSpace(value) ? throw new FormatException($"{path} must not be empty.") : value;
    }

    private static bool Boolean(JsonElement element, string path) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new FormatException($"{path} must be true or false."),
    };
}

/// <summary>A plugin configuration file cannot be read or is invalid.</summary>
public sealed class PluginConfigurationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PluginConfigurationException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong.</param>
    public PluginConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="innerException">Cause.</param>
    public PluginConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
