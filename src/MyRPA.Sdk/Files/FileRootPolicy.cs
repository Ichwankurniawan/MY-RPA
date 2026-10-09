using MyRPA.Workflow.Execution;

namespace MyRPA.Sdk.Files;

/// <summary>Error types of file access through a <see cref="FileRootPolicy"/> (ADR-0042).</summary>
public static class FileErrorTypes
{
    /// <summary>The path is empty or not a valid path.</summary>
    public const string InvalidPath = "InvalidPath";

    /// <summary>The path leaves the file root, or goes through a symbolic link or junction.</summary>
    public const string FileAccessDenied = "FileAccessDenied";

    /// <summary>The file or folder does not exist.</summary>
    public const string FileNotFound = "FileNotFound";

    /// <summary>The destination exists and overwriting was not allowed.</summary>
    public const string FileAlreadyExists = "FileAlreadyExists";

    /// <summary>The file is larger than the configured limit.</summary>
    public const string FileTooLarge = "FileTooLarge";

    /// <summary>More items or rows than the configured limit.</summary>
    public const string TooManyItems = "TooManyItems";

    /// <summary>The operating system refused or failed the operation (in use, read-only, disk full…).</summary>
    public const string FileIoError = "FileIoError";
}

/// <summary>
/// Confines file access to one directory tree (ADR-0042, the pattern of ADR-0017). Every path is resolved against the
/// root; a path outside it, or one that goes through a symbolic link or junction between the root and the target, is
/// refused. Messages name the path as the workflow gave it, never the absolute root. Immutable and thread-safe.
/// </summary>
/// <remarks>
/// A link created inside the root after a path was resolved is a race this check cannot close; the root should be a
/// folder only the robot's account can write to.
/// </remarks>
public sealed class FileRootPolicy
{
    /// <summary>Creates the policy.</summary>
    /// <param name="root">An existing directory.</param>
    /// <exception cref="ArgumentException">The root does not exist.</exception>
    public FileRootPolicy(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Root))
        {
            throw new ArgumentException("The file root does not exist.", nameof(root));
        }
    }

    /// <summary>The only directory tree the activities may read from or write to.</summary>
    public string Root { get; }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// The full path of <paramref name="path"/> (absolute, or relative to the root) when it is inside the root and
    /// reached without links. The target itself need not exist.
    /// </summary>
    /// <param name="path">The path as the workflow gave it.</param>
    /// <returns>The full path.</returns>
    /// <exception cref="ActivityFailedException"><see cref="FileErrorTypes.InvalidPath"/> or <see cref="FileErrorTypes.FileAccessDenied"/>.</exception>
    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ActivityFailedException(FileErrorTypes.InvalidPath, "A file path is empty.");
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, Root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ActivityFailedException(FileErrorTypes.InvalidPath, $"'{path}' is not a valid path.", ex);
        }

        // A drive root (C:\) keeps its separator after trimming; any other root gets one, so C:\data never admits C:\data2.
        var prefix = Path.EndsInDirectorySeparator(Root) ? Root : Root + Path.DirectorySeparatorChar;
        if (!string.Equals(full, Root, PathComparison) && !full.StartsWith(prefix, PathComparison))
        {
            throw new ActivityFailedException(FileErrorTypes.FileAccessDenied, $"'{path}' is outside the file root.");
        }

        for (var current = full; !string.Equals(current, Root, PathComparison); current = Path.GetDirectoryName(current)!)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                throw new ActivityFailedException(FileErrorTypes.FileAccessDenied, $"'{path}' goes through a link; links are not allowed.");
            }
        }

        return full;
    }

    /// <summary>An existing file inside the root.</summary>
    /// <param name="path">The path as the workflow gave it.</param>
    /// <returns>The full path.</returns>
    public string ResolveExistingFile(string path)
    {
        var full = Resolve(path);
        return File.Exists(full) ? full : throw new ActivityFailedException(FileErrorTypes.FileNotFound, $"The file '{path}' does not exist.");
    }

    /// <summary>An existing folder inside the root (the root itself is allowed).</summary>
    /// <param name="path">The path as the workflow gave it.</param>
    /// <returns>The full path.</returns>
    public string ResolveExistingFolder(string path)
    {
        var full = Resolve(path);
        return Directory.Exists(full) ? full : throw new ActivityFailedException(FileErrorTypes.FileNotFound, $"The folder '{path}' does not exist.");
    }

    /// <summary>
    /// A file to write: inside the root, not a folder, and only an existing file when <paramref name="overwrite"/> is
    /// true. Its folder is created when missing (inside the root).
    /// </summary>
    /// <param name="path">The path as the workflow gave it.</param>
    /// <param name="overwrite">Whether an existing file may be replaced.</param>
    /// <returns>The full path.</returns>
    public string ResolveFileToWrite(string path, bool overwrite)
    {
        var full = Resolve(path);
        if (string.Equals(full, Root, PathComparison) || Directory.Exists(full))
        {
            throw new ActivityFailedException(FileErrorTypes.InvalidPath, $"'{path}' is a folder; give a file path.");
        }

        if (File.Exists(full) && !overwrite)
        {
            throw new ActivityFailedException(FileErrorTypes.FileAlreadyExists, $"The file '{path}' exists; set 'overwrite' to replace it.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    /// <summary>The path relative to the root, with '/' separators (what activities return to workflows).</summary>
    /// <param name="fullPath">A full path inside the root.</param>
    /// <returns>The relative path.</returns>
    public string Relative(string fullPath) => Path.GetRelativePath(Root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
}
