using System.Globalization;
using MyRPA.Sdk.Files;
using MyRPA.Sdk.Plugins;

namespace MyRPA.Sftp;

/// <summary>An SFTP server named in the plugin configuration; workflows refer to it by name only.</summary>
/// <param name="Name">The name workflows use.</param>
/// <param name="Host">Its host name.</param>
/// <param name="Port">Its port.</param>
/// <param name="HostKey">The pinned host key fingerprint, as <c>ssh-keygen -lf</c> prints it: <c>SHA256:…</c>.</param>
/// <param name="Username">The default user (an activity may give another).</param>
/// <param name="PrivateKeyPath">A private key file for key login, or null.</param>
/// <param name="RemoteRoot">The only remote folder tree the activities may use (an absolute path, "/" for all).</param>
public sealed record SftpServer(string Name, string Host, int Port, string HostKey, string? Username, string? PrivateKeyPath, string RemoteRoot);

/// <summary>Plugin settings, built once in <see cref="SftpPlugin.Initialize"/>.</summary>
/// <param name="Servers">The named servers.</param>
/// <param name="Files">The local folder tree downloads write to and uploads read from; null when fileRoot is not set.</param>
/// <param name="MaxFileBytes">The largest file transferred.</param>
/// <param name="TimeoutMs">The timeout of one activity's conversation with the server.</param>
public sealed record SftpOptions(IReadOnlyDictionary<string, SftpServer> Servers, FileRootPolicy? Files, long MaxFileBytes, int TimeoutMs)
{
    /// <summary>The local file policy, or a FileAccessDenied failure when the plugin has no fileRoot.</summary>
    /// <param name="activity">The activity type, for the message.</param>
    /// <returns>The policy.</returns>
    public FileRootPolicy RequireFiles(string activity) =>
        Files ?? throw new MyRPA.Workflow.Execution.ActivityFailedException(FileErrorTypes.FileAccessDenied, $"{activity} needs the SFTP plugin's fileRoot setting (the only local folder it may use); it is not set.");
}

/// <summary>
/// Entry point of the SFTP plugin (Phase 7.1, ADR-0043). Settings:
/// <list type="bullet">
/// <item><c>server.NAME.host</c>, <c>server.NAME.port</c> (22).</item>
/// <item><c>server.NAME.hostKey</c> — required: the server's key fingerprint (<c>SHA256:…</c>); another key is refused.</item>
/// <item><c>server.NAME.username</c>, <c>server.NAME.privateKeyPath</c> — optional; the password and passphrase are secret properties.</item>
/// <item><c>server.NAME.remoteRoot</c> — the only remote folder tree the activities may use (default "/").</item>
/// <item><c>fileRoot</c> — the local folder tree (not set: transfers are refused); <c>maxFileBytes</c> (500 MB); <c>timeoutMs</c> (60000).</item>
/// </list>
/// </summary>
public sealed class SftpPlugin : IPlugin
{
    private SftpOptions? _options;

    /// <inheritdoc />
    public void Initialize(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings;
        var servers = new Dictionary<string, SftpServer>(StringComparer.OrdinalIgnoreCase);
        var names = settings.Keys
            .Select(k => k.Split('.'))
            .Where(p => p.Length == 3 && p[0].Equals("server", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1])
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            string? Optional(string field) => settings.FirstOrDefault(s => s.Key.Equals($"server.{name}.{field}", StringComparison.OrdinalIgnoreCase)).Value is { } v && v.Trim().Length > 0 ? v.Trim() : null;
            string Required(string field, string why) => Optional(field) ?? throw new ArgumentException($"SFTP server '{name}' needs the setting 'server.{name}.{field}' ({why}).", nameof(context));
            var hostKey = Required("hostKey", "the server's key fingerprint, such as SHA256:… from ssh-keygen -lf; an unpinned server is never trusted");
            if (!hostKey.StartsWith("SHA256:", StringComparison.Ordinal) || hostKey.Length < 20)
            {
                throw new ArgumentException($"'server.{name}.hostKey' must be a SHA256 fingerprint such as SHA256:nThbg6kXUpJWGl7E1IGOCspRomTxdCARLviKw6E5SY8.", nameof(context));
            }

            var port = Optional("port") is { } portText
                ? int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535 ? p : throw new ArgumentException($"'server.{name}.port' must be a port number.", nameof(context))
                : 22;
            var root = Optional("remoteRoot") ?? "/";
            if (!root.StartsWith('/'))
            {
                throw new ArgumentException($"'server.{name}.remoteRoot' must be an absolute remote path such as /upload.", nameof(context));
            }

            servers[name] = new SftpServer(name, Required("host", "its host name"), port, hostKey, Optional("username"), Optional("privateKeyPath"), RemotePaths.Normalize(root));
        }

        _options = new SftpOptions(
            servers,
            settings.TryGetValue("fileRoot", out var fileRoot) && fileRoot.Trim().Length > 0 ? new FileRootPolicy(fileRoot.Trim()) : null,
            Number(settings, "maxFileBytes", 500L * 1024 * 1024, 1, long.MaxValue),
            (int)Number(settings, "timeoutMs", 60_000, 1, 3_600_000));
    }

    /// <inheritdoc />
    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        registrar
            .AddInstance(_options ?? throw new InvalidOperationException("Initialize must run before Register."))
            .AddActivity<SftpListActivity>(SftpListActivity.Descriptor)
            .AddActivity<SftpDownloadActivity>(SftpDownloadActivity.Descriptor)
            .AddActivity<SftpUploadActivity>(SftpUploadActivity.Descriptor)
            .AddActivity<SftpDeleteActivity>(SftpDeleteActivity.Descriptor)
            .AddActivity<SftpMoveActivity>(SftpMoveActivity.Descriptor)
            .AddActivity<SftpCreateFolderActivity>(SftpCreateFolderActivity.Descriptor);
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

/// <summary>Remote paths confined to a server's remote root ('/' separated, no escape through '..').</summary>
internal static class RemotePaths
{
    /// <summary>An absolute remote path with '.' and '..' resolved; null when '..' climbs above '/'.</summary>
    public static string? TryNormalize(string path)
    {
        var parts = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(segment);
        }

        return "/" + string.Join('/', parts);
    }

    public static string Normalize(string path) => TryNormalize(path) ?? "/";

    /// <summary>The full remote path of <paramref name="path"/> (relative to the root, or absolute inside it), or null when it leaves the root.</summary>
    public static string? Resolve(string root, string path)
    {
        if (path.Any(char.IsControl))
        {
            return null;
        }

        var combined = path.StartsWith('/') ? path : (root == "/" ? "/" : root + "/") + path;
        var full = TryNormalize(combined);
        return full is not null && (root == "/" || full == root || full.StartsWith(root + "/", StringComparison.Ordinal)) ? full : null;
    }

    /// <summary>The path as messages and results show it: relative to the root.</summary>
    public static string Relative(string root, string full) =>
        root == "/" ? full.TrimStart('/') : full.Length > root.Length ? full[(root.Length + 1)..] : ".";
}
