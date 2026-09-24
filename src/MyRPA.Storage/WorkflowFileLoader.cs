using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Validation;

namespace MyRPA.Storage;

/// <summary>Reads a workflow file and runs it through <see cref="WorkflowLoader"/>. Stateless.</summary>
/// <param name="loader">The validation pipeline.</param>
public sealed class WorkflowFileLoader(WorkflowLoader loader)
{
    /// <summary>Maximum accepted workflow file size (5 MB).</summary>
    public const long MaxFileSizeBytes = 5 * 1024 * 1024;

    private readonly WorkflowLoader _loader = loader ?? throw new ArgumentNullException(nameof(loader));

    /// <summary>Loads and validates a workflow file.</summary>
    /// <param name="path">File path (relative paths are resolved against the current directory).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<WorkflowResolution> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return WorkflowResolution.Failure($"'{path}' is not a valid path.");
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            return WorkflowResolution.Failure($"'{path}' is not a .json file.", fullPath);
        }

        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            return WorkflowResolution.Failure($"File '{fullPath}' does not exist.", fullPath);
        }

        if (file.LinkTarget is not null)
        {
            return WorkflowResolution.Failure($"'{fullPath}' is a symbolic link; links are not followed.", fullPath);
        }

        if (file.Length > MaxFileSizeBytes)
        {
            return WorkflowResolution.Failure($"'{fullPath}' is larger than {MaxFileSizeBytes / (1024 * 1024)} MB.", fullPath);
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            return WorkflowResolution.Failure($"Cannot read '{fullPath}': {ex.Message}", fullPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            return WorkflowResolution.Failure($"Cannot read '{fullPath}': {ex.Message}", fullPath);
        }

        var result = _loader.Load(json);
        return result.IsValid
            ? WorkflowResolution.Success(result.Workflow, fullPath, result.Diagnostics)
            : WorkflowResolution.Failure("The workflow is invalid.", fullPath, result.Diagnostics);
    }
}
