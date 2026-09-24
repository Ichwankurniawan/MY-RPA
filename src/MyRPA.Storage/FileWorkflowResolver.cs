using System.Collections.Concurrent;
using MyRPA.Workflow.Execution;

namespace MyRPA.Storage;

/// <summary>
/// Resolves <c>Core.InvokeWorkflow</c> references to files (ADR-0012): relative paths only, resolved against the
/// invoking workflow's directory, confined to the entry workflow's directory, <c>.json</c> only, no symbolic links.
/// Registered <b>scoped</b>: one instance (and cache) per top-level run; nothing is cached globally.
/// </summary>
/// <param name="files">File loader.</param>
public sealed class FileWorkflowResolver(WorkflowFileLoader files) : IWorkflowResolver
{
    private static readonly StringComparison _pathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly WorkflowFileLoader _files = files ?? throw new ArgumentNullException(nameof(files));
    private readonly ConcurrentDictionary<string, WorkflowResolution> _cache = new(
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <inheritdoc />
    public async ValueTask<WorkflowResolution> ResolveAsync(string reference, string invokingLocation, string rootLocation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(invokingLocation);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootLocation);

        if (Path.IsPathRooted(reference) || reference.Contains(':', StringComparison.Ordinal))
        {
            return WorkflowResolution.Failure($"'{reference}' must be a relative path.");
        }

        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(invokingLocation))!;
        var rootDirectory = Path.GetDirectoryName(Path.GetFullPath(rootLocation))!;
        var fullPath = Path.GetFullPath(Path.Combine(baseDirectory, reference));

        if (!IsWithin(fullPath, rootDirectory))
        {
            return WorkflowResolution.Failure($"'{reference}' resolves outside the workflow root '{rootDirectory}'.", fullPath);
        }

        if (_cache.TryGetValue(fullPath, out var cached))
        {
            return cached;
        }

        var resolution = await _files.LoadAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return _cache.GetOrAdd(fullPath, resolution);
    }

    private static bool IsWithin(string path, string directory)
    {
        var prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, _pathComparison);
    }
}
