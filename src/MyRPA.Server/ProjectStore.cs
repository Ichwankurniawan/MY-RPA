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
    NotFound,
    PreconditionFailed,
    PreconditionRequired,
}

/// <summary>
/// The server's view of project folders (ADR-0025): only <c>.json</c> files inside registered project roots, addressed
/// by relative paths that are normalized and confined like ADR-0012. Writes are conditional on the file's ETag, so a
/// stale editor cannot overwrite newer content.
/// </summary>
internal sealed class ProjectStore(ServerOptions options) : IDisposable
{
    private static readonly string[] _skippedFolders = ["bin", "obj", "node_modules"];
    private readonly SemaphoreSlim _writes = new(1, 1);

    public IReadOnlyList<ProjectRoot> Projects => options.Projects;

    /// <summary>Resolves a project-relative workflow path to a confined full path.</summary>
    public bool TryResolve(string project, string? relativePath, out string fullPath, out string error)
    {
        fullPath = string.Empty;
        var root = options.Projects.FirstOrDefault(p => string.Equals(p.Name, project, StringComparison.OrdinalIgnoreCase));
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

        if (!relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
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
