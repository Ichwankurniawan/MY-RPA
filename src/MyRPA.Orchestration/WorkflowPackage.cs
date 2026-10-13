using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyRPA.Workflow;
using MyRPA.Workflow.Validation;

namespace MyRPA.Orchestration;

/// <summary>A package as stored and downloaded: its manifest and its workflow files (ADR-0049).</summary>
public sealed record PackageDocument
{
    /// <summary>Package format (<see cref="WorkflowPackage.FormatName"/>).</summary>
    public required string Format { get; init; }

    /// <summary>The manifest.</summary>
    public required PackageManifest Manifest { get; init; }

    /// <summary>The workflow files, ordered by path.</summary>
    public IReadOnlyList<PackageContentFile> Files { get; init; } = [];
}

/// <summary>A workflow file of a package.</summary>
/// <param name="Path">Package-relative path with <c>/</c>.</param>
/// <param name="Sha256">SHA-256 (hex) of the UTF-8 content.</param>
/// <param name="Content">The workflow JSON.</param>
public sealed record PackageContentFile(string Path, string Sha256, string Content);

/// <summary>What to publish.</summary>
public sealed record PackageBuildRequest
{
    /// <summary>The process.</summary>
    public required string ProcessId { get; init; }

    /// <summary>The process name.</summary>
    public required string Name { get; init; }

    /// <summary>The new version.</summary>
    public required int Version { get; init; }

    /// <summary>The project.</summary>
    public required string Project { get; init; }

    /// <summary>The project-relative paths of the workflows a job may start (at least one).</summary>
    public required IReadOnlyList<string> EntryPaths { get; init; }

    /// <summary>The project's workflow files (project-relative path with <c>/</c>, content).</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Files { get; init; }

    /// <summary>Publication time.</summary>
    public required DateTimeOffset PublishedAt { get; init; }

    /// <summary>Who publishes.</summary>
    public required string PublishedBy { get; init; }

    /// <summary>Release notes.</summary>
    public string? Notes { get; init; }
}

/// <summary>A built package, or why it could not be built.</summary>
/// <param name="Errors">Why publishing was refused (empty on success).</param>
/// <param name="Content">The package bytes.</param>
/// <param name="Sha256">Their SHA-256 (hex).</param>
/// <param name="Manifest">The manifest.</param>
public sealed record PackageBuildResult(IReadOnlyList<string> Errors, byte[] Content, string Sha256, PackageManifest? Manifest)
{
    /// <summary>Whether the package was built.</summary>
    public bool Succeeded => Errors.Count == 0 && Manifest is not null;
}

/// <summary>
/// Builds and verifies workflow packages (ADR-0049). A package is one project: its entry points (the workflows a
/// job may start) and every valid workflow file of the project (sub-workflows and agent tool workflows resolve from the package as they did from the project), with a
/// manifest naming each file's hash, the activity types used and the plugins that provide them. Packages never hold
/// plugin binaries, plugin settings or secrets: plugins stay installed on Runners, and workflow files cannot contain
/// secret values (MYRPA1066).
/// </summary>
public static class WorkflowPackage
{
    /// <summary>The package format this assembly writes and reads.</summary>
    public const string FormatName = "myrpa-package/2";

    /// <summary>The first package format (one entry workflow per package); Runners that do not say what they read read only this.</summary>
    public const string FirstFormatName = "myrpa-package/1";

    /// <summary>Most files in a package.</summary>
    public const int MaxFiles = 500;

    /// <summary>Most entry points in a package.</summary>
    public const int MaxEntryPoints = 100;

    /// <summary>Largest package.</summary>
    public const long MaxBytes = 20 * 1024 * 1024;

    /// <summary>Builds a package.</summary>
    /// <param name="request">What to publish.</param>
    /// <param name="load">The workflow loader (validation against the activity catalog).</param>
    /// <param name="pluginOf">The plugin providing an activity type (null for built-ins).</param>
    public static PackageBuildResult Build(PackageBuildRequest request, Func<string, WorkflowLoadResult> load, Func<string, PackagePlugin?> pluginOf)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(pluginOf);
        var errors = new List<string>();
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, content) in request.Files)
        {
            if (!IsSafePath(path))
            {
                errors.Add($"'{path}' is not a safe relative path.");
            }
            else if (!seen.Add(path))
            {
                errors.Add($"'{path}' appears twice.");
            }
            else
            {
                files[path] = content;
            }
        }

        if (request.EntryPaths.Count == 0)
        {
            errors.Add("Choose at least one entry point.");
            return Fail(errors);
        }

        if (request.EntryPaths.Count > MaxEntryPoints)
        {
            errors.Add($"A package has at most {MaxEntryPoints} entry points.");
            return Fail(errors);
        }

        var entryWorkflows = new Dictionary<string, WorkflowDefinition>(StringComparer.Ordinal);
        foreach (var entryPath in request.EntryPaths)
        {
            if (entryWorkflows.ContainsKey(entryPath))
            {
                errors.Add($"The entry point '{entryPath}' appears twice.");
            }
            else if (!files.TryGetValue(entryPath, out var entryJson))
            {
                errors.Add($"The entry point '{entryPath}' is not in the project.");
            }
            else
            {
                var entry = load(entryJson);
                if (entry.Workflow is { } entryWorkflow)
                {
                    entryWorkflows[entryPath] = entryWorkflow;
                }
                else if (entry.Errors.Any())
                {
                    errors.AddRange(entry.Errors.Select(d => $"{entryPath}: {d.Code} {d.Message}"));
                }
                else
                {
                    errors.Add($"{entryPath} is not a valid workflow.");
                }
            }
        }

        if (errors.Count > 0)
        {
            return Fail(errors);
        }

        var included = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var excluded = new List<string>();
        var activities = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (path, content) in files)
        {
            var workflow = entryWorkflows.TryGetValue(path, out var known) ? known : load(content).Workflow;
            if (workflow is null)
            {
                excluded.Add($"{path}: not a valid workflow");
                continue;
            }

            included[path] = content;
            foreach (var node in workflow.Root.DescendantsAndSelf())
            {
                activities.Add(node.Type.Value);
            }
        }

        if (included.Count > MaxFiles)
        {
            errors.Add($"A package holds at most {MaxFiles} workflow files; the project has {included.Count}.");
        }

        var plugins = activities.Select(pluginOf).OfType<PackagePlugin>().DistinctBy(p => p.Id, StringComparer.Ordinal).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        var contentFiles = included.Select(f => new PackageContentFile(f.Key, Sha256Hex(Encoding.UTF8.GetBytes(f.Value)), f.Value)).ToList();
        var manifest = new PackageManifest
        {
            ProcessId = request.ProcessId,
            Name = request.Name,
            Version = request.Version,
            Project = request.Project,
            EntryPoints = [.. request.EntryPaths.Select(path => EntryPoint(path, entryWorkflows[path]))],
            Files = [.. contentFiles.Select(f => new PackageFile(f.Path, f.Sha256, Encoding.UTF8.GetByteCount(f.Content)))],
            Activities = [.. activities],
            Plugins = plugins,
            Excluded = excluded,
            PublishedAt = request.PublishedAt,
            PublishedBy = request.PublishedBy,
            Notes = request.Notes,
        };
        var document = new PackageDocument { Format = FormatName, Manifest = manifest, Files = contentFiles };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, OrchestrationJson.Default.PackageDocument);
        if (bytes.Length > MaxBytes)
        {
            errors.Add($"The package is {bytes.Length:N0} bytes; at most {MaxBytes:N0} are allowed.");
        }

        return errors.Count > 0 ? Fail(errors) : new PackageBuildResult([], bytes, Sha256Hex(bytes), manifest);
    }

    /// <summary>
    /// Verifies downloaded package bytes against the hash the Server assigned, then every file against the manifest.
    /// </summary>
    /// <param name="content">The bytes.</param>
    /// <param name="expectedSha256">The hash from the job assignment.</param>
    /// <param name="document">The package, when valid.</param>
    /// <param name="error">Why it is not, otherwise.</param>
    public static bool TryVerify(ReadOnlySpan<byte> content, string expectedSha256, out PackageDocument? document, out string? error)
    {
        document = null;
        if (!string.Equals(Sha256Hex(content), expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            error = "The package hash does not match the published hash.";
            return false;
        }

        // The format first: a package from a newer (or older) Server says so plainly instead of failing to deserialize.
        if (ReadFormat(content) is { } format && format != FormatName)
        {
            error = $"The package uses format {format}; this Runner reads {FormatName}. Update the Runner and the Server to the same version.";
            return false;
        }

        PackageDocument? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(content, OrchestrationJson.Default.PackageDocument);
        }
        catch (JsonException ex)
        {
            error = $"The package is not readable: {ex.Message}";
            return false;
        }

        if (parsed is null || parsed.Format != FormatName || parsed.Manifest.Format != FormatName)
        {
            error = $"The package format is not {FormatName}.";
            return false;
        }

        var listed = parsed.Manifest.Files.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.Ordinal);
        if (listed.Count != parsed.Files.Count)
        {
            error = "The package files do not match its manifest.";
            return false;
        }

        foreach (var file in parsed.Files)
        {
            if (!IsSafePath(file.Path) || !listed.TryGetValue(file.Path, out var hash)
                || !string.Equals(hash, file.Sha256, StringComparison.Ordinal)
                || !string.Equals(Sha256Hex(Encoding.UTF8.GetBytes(file.Content)), hash, StringComparison.Ordinal))
            {
                error = $"The package file '{file.Path}' does not match its manifest.";
                return false;
            }
        }

        if (parsed.Manifest.EntryPoints is not { Count: > 0 } entryPoints || entryPoints.Any(e => e is null || !listed.ContainsKey(e.Path)))
        {
            error = "The package's entry points are not among its files.";
            return false;
        }

        document = parsed;
        error = null;
        return true;
    }

    /// <summary>The top-level <c>format</c> of package bytes, or null when it cannot be read.</summary>
    public static string? ReadFormat(ReadOnlySpan<byte> content)
    {
        try
        {
            var reader = new Utf8JsonReader(content);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isFormat = reader.ValueTextEquals("format"u8);
                if (!reader.Read())
                {
                    return null;
                }

                if (isFormat)
                {
                    return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                }

                reader.Skip();
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether a package path is relative, uses <c>/</c>, and never leaves the package.</summary>
    public static bool IsSafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 400 || path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal)
            || path.StartsWith('/') || path.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        return path.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");
    }

    /// <summary>SHA-256 (lowercase hex).</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Whether <paramref name="installed"/> satisfies <paramref name="required"/> (same or newer version).</summary>
    public static bool Satisfies(string installed, string required) =>
        Version.TryParse(Numeric(installed), out var have) && Version.TryParse(Numeric(required), out var need)
            ? have >= need
            : string.Equals(installed, required, StringComparison.Ordinal);

    private static string Numeric(string version)
    {
        // Semantic versions may carry a pre-release or build suffix; compare the numeric part.
        var end = version.IndexOfAny(['-', '+']);
        return end < 0 ? version : version[..end];
    }

    /// <summary>The entry point of a package, or null when the package has no such entry point.</summary>
    public static PackageEntryPoint? FindEntryPoint(PackageManifest manifest, string entryPath)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.EntryPoints.FirstOrDefault(e => string.Equals(e.Path, entryPath, StringComparison.Ordinal));
    }

    private static PackageEntryPoint EntryPoint(string path, WorkflowDefinition workflow) => new()
    {
        Path = path,
        WorkflowId = workflow.Id.Value,
        Name = workflow.Name,
        WorkflowVersion = workflow.Version,
        SchemaVersion = workflow.SchemaVersion.ToString(),
        Arguments = [.. workflow.Arguments.Select(a => new PackageArgument(a.Name, a.Direction.ToString(), a.Type.ToString(), a.IsRequired && a.DefaultValue is null, workflow.SecretNames.Contains(a.Name))
        {
            Default = a.DefaultValue is null || workflow.SecretNames.Contains(a.Name) ? null : DefaultJson(a.DefaultValue),
        })],
    };

    private static string DefaultJson(object value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            MyRPA.Workflow.Values.WorkflowValues.WriteJson(writer, value);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static PackageBuildResult Fail(List<string> errors) => new(errors, [], string.Empty, null);
}
