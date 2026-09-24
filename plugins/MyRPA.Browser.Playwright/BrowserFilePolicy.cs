using MyRPA.Workflow.Execution;

namespace MyRPA.Browser.Playwright;

/// <summary>
/// Confines <c>Browser.UploadFile</c> and <c>Browser.DownloadFile</c> to one directory tree (ADR-0017). Paths are resolved
/// against <see cref="Root"/>; the result must stay inside it, and no file or directory on the way may be a link.
/// The root is the plugin setting <c>fileRoot</c>, or the host's current working directory when not set.
/// </summary>
public sealed class BrowserFilePolicy
{
    private static readonly StringComparison _pathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Creates the policy.</summary>
    /// <param name="root">An existing directory.</param>
    public BrowserFilePolicy(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Root))
        {
            throw new ArgumentException($"The browser file root '{Root}' does not exist.", nameof(root));
        }
    }

    /// <summary>The only directory tree browser activities may read from or write to.</summary>
    public string Root { get; }

    /// <summary>Resolves a file to upload: must exist inside the root.</summary>
    /// <param name="path">Absolute path, or relative to <see cref="Root"/>.</param>
    public string ResolveUpload(string path)
    {
        var full = Resolve(path);
        if (!File.Exists(full))
        {
            throw new ActivityFailedException(BrowserErrorTypes.FileNotFound, $"The file to upload '{path}' does not exist.");
        }

        return full;
    }

    /// <summary>Resolves a download destination: its directory must exist inside the root.</summary>
    /// <param name="path">Absolute path, or relative to <see cref="Root"/>.</param>
    /// <param name="overwrite">Whether an existing file may be replaced.</param>
    public string ResolveDownload(string path, bool overwrite)
    {
        var full = Resolve(path);
        if (Directory.Exists(full))
        {
            throw new ActivityFailedException(BrowserErrorTypes.FileAccessDenied, $"The download destination '{path}' is a directory; give a file path.");
        }

        if (!Directory.Exists(Path.GetDirectoryName(full)))
        {
            throw new ActivityFailedException(BrowserErrorTypes.FileAccessDenied, $"The directory of the download destination '{path}' does not exist.");
        }

        if (File.Exists(full) && !overwrite)
        {
            throw new ActivityFailedException(BrowserErrorTypes.FileAlreadyExists, $"The download destination '{path}' exists; set 'overwrite' to replace it.");
        }

        return full;
    }

    private string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ActivityFailedException(BrowserErrorTypes.InvalidArgument, "A file path is empty.");
        }

        string full;
        try
        {
            full = Path.GetFullPath(path, Root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ActivityFailedException(BrowserErrorTypes.InvalidArgument, $"'{path}' is not a valid path.", ex);
        }

        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, _pathComparison))
        {
            throw new ActivityFailedException(BrowserErrorTypes.FileAccessDenied, $"'{path}' is outside the browser file root '{Root}'.");
        }

        // Reject links anywhere between the root and the target, so the confinement cannot be escaped through them.
        for (var current = full; !string.Equals(current, Root, _pathComparison); current = Path.GetDirectoryName(current)!)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                throw new ActivityFailedException(BrowserErrorTypes.FileAccessDenied, $"'{path}' goes through a link; links are not allowed.");
            }
        }

        return full;
    }
}
