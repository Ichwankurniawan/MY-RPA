using System.Security.Cryptography;
using System.Text;

namespace MyRPA.Plugins.Loading;

/// <summary>
/// The verified content of one plugin directory: every file's SHA-256 and a digest of the whole directory
/// (ADR-0015). Files are hashed, never loaded, here. Links anywhere in the directory are rejected so that the content
/// cannot point outside it.
/// </summary>
internal sealed class PluginDirectory
{
    public const int MaxFiles = 2048;

    public const long MaxTotalBytes = 256L * 1024 * 1024;

    private PluginDirectory(string path, IReadOnlyDictionary<string, string> fileHashes, string digest)
    {
        Path = path;
        FileHashes = fileHashes;
        Digest = digest;
    }

    /// <summary>Full path of the directory.</summary>
    public string Path { get; }

    /// <summary>Relative path ('/' separators) to lowercase hex SHA-256, for every file.</summary>
    public IReadOnlyDictionary<string, string> FileHashes { get; }

    /// <summary>
    /// SHA-256 over the sorted lines <c>"&lt;relative path&gt;\n&lt;file sha256&gt;\n"</c> of every file, as lowercase hex.
    /// </summary>
    public string Digest { get; }

    public static PluginDirectory? Inspect(string path, out string? problem)
    {
        problem = null;
        var root = new DirectoryInfo(path);
        if (!root.Exists)
        {
            problem = $"Directory '{path}' does not exist.";
            return null;
        }

        if (IsLink(root))
        {
            problem = $"'{path}' is a link; plugin directories must be real directories.";
            return null;
        }

        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
        foreach (var entry in root.EnumerateFileSystemInfos("*", options))
        {
            var relative = System.IO.Path.GetRelativePath(root.FullName, entry.FullName).Replace('\\', '/');
            if (IsLink(entry))
            {
                problem = $"'{relative}' is a link; links are not allowed inside plugin directories.";
                return null;
            }

            if (entry is not FileInfo file)
            {
                continue;
            }

            total += file.Length;
            if (hashes.Count >= MaxFiles || total > MaxTotalBytes)
            {
                problem = $"The directory has more than {MaxFiles} files or {MaxTotalBytes / (1024 * 1024)} MB.";
                return null;
            }

            hashes.Add(relative, HashFile(file.FullName));
        }

        var lines = new StringBuilder();
        foreach (var (relative, hash) in hashes)
        {
            lines.Append(relative).Append('\n').Append(hash).Append('\n');
        }

        return new PluginDirectory(root.FullName, hashes, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(lines.ToString()))));
    }

    public static string Hex(byte[] hash) => Convert.ToHexStringLower(hash);

    public bool IsInside(string fullPath)
    {
        var prefix = System.IO.Path.EndsInDirectorySeparator(Path) ? Path : Path + System.IO.Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public string RelativePath(string fullPath) => System.IO.Path.GetRelativePath(Path, fullPath).Replace('\\', '/');

    private static bool IsLink(FileSystemInfo info) => info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Hex(SHA256.HashData(stream));
    }
}
