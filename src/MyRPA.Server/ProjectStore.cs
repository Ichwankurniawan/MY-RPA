using System.Security.Cryptography;
using System.Text.Json;

namespace MyRPA.Server;

/// <summary>A workflow file in a project.</summary>
/// <param name="Path">Path relative to the project root, with <c>/</c> separators.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Modified">Last write time.</param>
internal sealed record ProjectFileInfo(string Path, long Size, DateTimeOffset Modified);

/// <summary>Outcome of a conditional write or delete.</summary>
internal enum FileWriteOutcome
{
    Created,
    Updated,
    Deleted,
    Moved,
    TargetExists,
    NotFound,
    PreconditionFailed,
    PreconditionRequired,
}

/// <summary>
/// The server's view of project folders (ADR-0025): only <c>.json</c> files inside registered project roots, addressed
/// by relative paths that are normalized and confined like ADR-0012. Writes are conditional on the file's ETag, so a
/// stale editor cannot overwrite newer content.
/// </summary>
internal sealed class ProjectStore(ServerOptions options, TimeProvider time) : IDisposable
{
    /// <summary>Where deleted projects go, inside the projects folder (ADR-0046); hidden, so never a project itself.</summary>
    public const string TrashFolder = ".trash";

    private static readonly string[] _skippedFolders = ["bin", "obj", "node_modules"];
    private static readonly string[] _reservedNames = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
    private readonly SemaphoreSlim _writes = new(1, 1);

    /// <summary>The projects: the <c>--project</c> folders, then the folders of the projects folder by name (ADR-0046).</summary>
    public IReadOnlyList<ProjectRoot> Projects
    {
        get
        {
            if (options.ProjectsRoot is not { } root || !Directory.Exists(root))
            {
                return options.Projects;
            }

            // A folder of the projects folder whose name a --project already uses is left out (the --project wins).
            var inRoot = Directory.EnumerateDirectories(root)
                .Select(d => new DirectoryInfo(d))
                .Where(d => !d.Name.StartsWith('.') && !_skippedFolders.Contains(d.Name, StringComparer.OrdinalIgnoreCase) && (d.Attributes & FileAttributes.ReparsePoint) == 0)
                .Where(d => !options.Projects.Any(p => string.Equals(p.Name, d.Name, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new ProjectRoot(d.Name, Path.TrimEndingDirectorySeparator(d.FullName), InRoot: true));
            return [.. options.Projects, .. inRoot];
        }
    }

    /// <summary>A project by name (case-insensitive); null when there is none.</summary>
    public ProjectRoot? Find(string project) => Projects.FirstOrDefault(p => string.Equals(p.Name, project, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether <paramref name="name"/> can name a new project folder on every platform: one segment, not hidden, no
    /// characters Windows refuses, no trailing dot or space, no device name, at most 100 characters.
    /// </summary>
    public static bool IsValidProjectName(string? name, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(name) || name.Trim() != name || name.Length > 100)
        {
            error = "A project name needs 1 to 100 characters, without leading or trailing spaces.";
        }
        else if (name.StartsWith('.') || name.EndsWith('.') || name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 || name.Any(char.IsControl))
        {
            error = "A project name cannot start or end with '.', or contain < > : \" / \\ | ? * or control characters.";
        }
        else if (_reservedNames.Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase))
        {
            error = $"'{name}' is a name Windows reserves for devices.";
        }

        return error.Length == 0;
    }

    /// <summary>Creates an empty project folder in the projects folder; never over an existing project, file or folder.</summary>
    public async Task<FileWriteOutcome> CreateProjectAsync(string name, CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var full = Path.Combine(options.ProjectsRoot!, name);
            if (Find(name) is not null || Directory.Exists(full) || File.Exists(full))
            {
                return FileWriteOutcome.TargetExists;
            }

            Directory.CreateDirectory(full);
            return FileWriteOutcome.Created;
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>
    /// Moves a project of the projects folder to its <c>.trash</c> (as <c>name-yyyyMMdd-HHmmss</c>), so it can be
    /// restored by hand. Returns the trash path relative to the projects folder, or why it was not moved.
    /// </summary>
    public async Task<(string? Trashed, string? Error)> TrashProjectAsync(ProjectRoot project, CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var trash = Path.Combine(options.ProjectsRoot!, TrashFolder);
            Directory.CreateDirectory(trash);
            var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var target = Path.Combine(trash, $"{project.Name}-{stamp}");
            for (var i = 2; Directory.Exists(target) || File.Exists(target); i++)
            {
                target = Path.Combine(trash, $"{project.Name}-{stamp}-{i}");
            }

            Directory.Move(project.Root, target);
            return (Path.GetRelativePath(options.ProjectsRoot!, target).Replace('\\', '/'), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file of the project is open elsewhere (Windows locks it), or the folder cannot be moved.
            return (null, $"The project folder cannot be moved to the trash: {ex.Message}");
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>Resolves a project-relative workflow path (or, with <paramref name="folder"/>, a folder path) to a confined full path.</summary>
    public bool TryResolve(string project, string? relativePath, out string fullPath, out string error, bool folder = false)
    {
        fullPath = string.Empty;
        var root = Find(project);
        if (root is null)
        {
            error = $"Unknown project '{project}'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\\', StringComparison.Ordinal) || relativePath.Contains(':', StringComparison.Ordinal)
            || relativePath.StartsWith('/') || relativePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            error = "The path must be a relative path with '/' separators.";
            return false;
        }

        var segments = relativePath.Split('/');
        if (segments.Any(s => s.Length == 0 || s is "." or ".." || s.StartsWith('.') || s.TrimEnd() != s))
        {
            error = "The path must not contain empty, '.', '..' or hidden segments.";
            return false;
        }

        if (!folder && !relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            error = "Only .json workflow files are served.";
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(root.Root, Path.Combine(segments)));
        var prefix = root.Root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            error = "The path leaves the project.";
            return false;
        }

        // A link inside the project could point anywhere: refuse any existing segment that is a reparse point.
        var current = root.Root;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            var info = new FileInfo(current);
            if (info.Exists || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    error = "Links are not followed.";
                    return false;
                }
            }
        }

        fullPath = candidate;
        error = string.Empty;
        return true;
    }

    /// <summary>The project's workflow files.</summary>
    public static IReadOnlyList<ProjectFileInfo> List(ProjectRoot project)
    {
        var files = new List<ProjectFileInfo>();
        var pending = new Stack<string>([project.Root]);
        while (pending.TryPop(out var folder))
        {
            foreach (var directory in Directory.EnumerateDirectories(folder))
            {
                var name = Path.GetFileName(directory);
                if (!name.StartsWith('.') && !_skippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase)
                    && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(directory);
                }
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                var info = new FileInfo(file);
                if (!info.Name.StartsWith('.') && (info.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    files.Add(new ProjectFileInfo(Path.GetRelativePath(project.Root, file).Replace('\\', '/'), info.Length, info.LastWriteTimeUtc));
                }
            }
        }

        return [.. files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The project's folders ('/'-separated, relative), empty ones included, so a new folder can be listed before it
    /// holds a workflow. Hidden, skipped and linked folders are left out, as in <see cref="List"/>.
    /// </summary>
    public static IReadOnlyList<string> ListFolders(ProjectRoot project)
    {
        var folders = new List<string>();
        var pending = new Stack<string>([project.Root]);
        while (pending.TryPop(out var folder))
        {
            foreach (var directory in Directory.EnumerateDirectories(folder))
            {
                var name = Path.GetFileName(directory);
                if (!name.StartsWith('.') && !_skippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase)
                    && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(directory);
                    folders.Add(Path.GetRelativePath(project.Root, directory).Replace('\\', '/'));
                }
            }
        }

        return [.. folders.Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Creates a folder (and its parents); never over an existing file or folder.</summary>
    public async Task<FileWriteOutcome> CreateFolderAsync(string fullPath, CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                return FileWriteOutcome.TargetExists;
            }

            Directory.CreateDirectory(fullPath);
            return FileWriteOutcome.Created;
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>Reads a file with its ETag; null when it does not exist or is too large.</summary>
    public async Task<(byte[] Content, string ETag)?> ReadAsync(string fullPath, CancellationToken cancellationToken)
    {
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length > options.MaxRequestBodyBytes)
        {
            return null;
        }

        var content = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return (content, ETagOf(content));
    }

    /// <summary>Writes a file if its current ETag matches <paramref name="ifMatch"/> (or it does not exist and <paramref name="ifNoneMatchAny"/>).</summary>
    public async Task<(FileWriteOutcome Outcome, string? ETag)> WriteAsync(string fullPath, byte[] content, string? ifMatch, bool ifNoneMatchAny, CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var exists = File.Exists(fullPath);
            if (ifMatch is null && !ifNoneMatchAny)
            {
                return (FileWriteOutcome.PreconditionRequired, null);
            }

            if (ifNoneMatchAny ? exists : !exists || ETagOf(await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false)) != ifMatch)
            {
                return (FileWriteOutcome.PreconditionFailed, null);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var temporary = fullPath + ".tmp-" + LocalSessions.NewSecret()[..8];
            await File.WriteAllBytesAsync(temporary, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, fullPath, overwrite: true);
            return (exists ? FileWriteOutcome.Updated : FileWriteOutcome.Created, ETagOf(content));
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>Deletes a file if its current ETag matches.</summary>
    public async Task<FileWriteOutcome> DeleteAsync(string fullPath, string? ifMatch, CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(fullPath))
            {
                return FileWriteOutcome.NotFound;
            }

            if (ifMatch is null)
            {
                return FileWriteOutcome.PreconditionRequired;
            }

            if (ETagOf(await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false)) != ifMatch)
            {
                return FileWriteOutcome.PreconditionFailed;
            }

            File.Delete(fullPath);
            return FileWriteOutcome.Deleted;
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>
    /// Renames or moves a file within its project if its current ETag matches. Never overwrites: an existing target is
    /// refused. The content (and so the ETag) is unchanged. A change of letter case only is allowed on Windows.
    /// </summary>
    public async Task<(FileWriteOutcome Outcome, string? ETag)> MoveAsync(string fromPath, string toPath, string? ifMatch, CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(fromPath))
            {
                return (FileWriteOutcome.NotFound, null);
            }

            if (ifMatch is null)
            {
                return (FileWriteOutcome.PreconditionRequired, null);
            }

            var etag = ETagOf(await File.ReadAllBytesAsync(fromPath, cancellationToken).ConfigureAwait(false));
            if (etag != ifMatch)
            {
                return (FileWriteOutcome.PreconditionFailed, null);
            }

            var caseOnly = OperatingSystem.IsWindows() && string.Equals(fromPath, toPath, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(toPath) || Directory.Exists(toPath)))
            {
                return (FileWriteOutcome.TargetExists, null);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(toPath)!);
            File.Move(fromPath, toPath, overwrite: false);
            return (FileWriteOutcome.Moved, etag);
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>Whether <paramref name="content"/> is a JSON object (the only shape a workflow file can have).</summary>
    public static bool IsJsonObject(byte[] content)
    {
        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void Dispose() => _writes.Dispose();

    public static string ETagOf(byte[] content) => $"\"{Convert.ToHexStringLower(SHA256.HashData(content))[..32]}\"";
}
