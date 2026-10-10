using System.IO.Compression;
using MyRPA.Core.Activities;
using MyRPA.Sdk.Files;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;
using static MyRPA.Files.FileValues;

namespace MyRPA.Files;

/// <summary>
/// <c>Zip.Create</c>: a ZIP archive of a file, a folder or a list of files (ADR-0043). The archive is written beside its
/// destination and moved into place when complete; links are skipped; at most the plugin's maxItems entries.
/// </summary>
public sealed class ZipCreateActivity(FilesOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Zip.Create"),
        "Create ZIP",
        FilesCategory,
        "Creates a ZIP archive from a file, a folder (its files matching a pattern, with their folders) or a List of files (stored by name). The archive appears only once complete; at most maxItems files; links are skipped.",
        [
            Input("source", ActivityValueType.Any, "A file or folder path, or a List of file paths, relative to the plugin's file root (or absolute inside it).", required: true),
            PathInput("destination", "The ZIP file to create."),
            Input("pattern", ActivityValueType.String, "For a folder: a name pattern with * and ?.", defaultJson: "\"*\""),
            Input("recursive", ActivityValueType.Boolean, "For a folder: also take files in sub-folders.", defaultJson: "true"),
            new("compression", ActivityPropertyKind.Text, isRequired: false, "How hard to compress.", ["Optimal", "Fastest", "SmallestSize", "None"]) { ValueType = ActivityValueType.String, DefaultValue = "\"Optimal\"" },
            Overwrite(),
            Output(ActivityValueType.Int, "Receives the number of files in the archive."),
        ])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var destinationText = Text(context, "destination");
        var overwrite = Flag(context, "overwrite", false);
        var destination = options.Files.ResolveFileToWrite(destinationText, overwrite);
        var entries = Entries(context, destination);
        var level = context.GetTextOrDefault("compression", "Optimal") switch
        {
            "Fastest" => CompressionLevel.Fastest,
            "SmallestSize" => CompressionLevel.SmallestSize,
            "None" => CompressionLevel.NoCompression,
            _ => CompressionLevel.Optimal,
        };

        var partial = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Path.GetRandomFileName()}.partial");
        try
        {
            await Io("create", destinationText, async () =>
            {
                var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await using (stream.ConfigureAwait(false))
                {
                    var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
                    await using (archive.ConfigureAwait(false))
                    {
                        foreach (var (full, name) in entries)
                        {
                            context.CancellationToken.ThrowIfCancellationRequested();
                            await archive.CreateEntryFromFileAsync(full, name, level, context.CancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                File.Move(partial, destination, overwrite);
            }).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(partial);
        }

        SetResult(context, (long)entries.Count);
        return ActivityResult.Completed;
    }

    /// <summary>The files to store and their names in the archive (the destination itself never included).</summary>
    private List<(string Full, string Name)> Entries(IActivityContext context, string destination)
    {
        var entries = new List<(string Full, string Name)>();
        switch (context.Evaluate("source"))
        {
            case string path:
                var full = options.Files.Resolve(path);
                if (File.Exists(full))
                {
                    entries.Add((full, Path.GetFileName(full)));
                }
                else if (Directory.Exists(full))
                {
                    var pattern = OptionalText(context, "pattern") ?? "*";
                    if (pattern.Length == 0 || pattern.Contains('/') || pattern.Contains('\\') || pattern.Contains(".."))
                    {
                        throw new ActivityFailedException(FileErrorTypes.InvalidPath, "'pattern' is a name pattern such as *.pdf; it cannot contain a folder.");
                    }

                    var enumeration = new EnumerationOptions
                    {
                        RecurseSubdirectories = Flag(context, "recursive", true),
                        AttributesToSkip = FileAttributes.ReparsePoint,
                        IgnoreInaccessible = true,
                        MatchType = MatchType.Simple,
                    };
                    foreach (var file in Directory.EnumerateFiles(full, pattern, enumeration).Order(StringComparer.Ordinal))
                    {
                        if (!string.Equals(file, destination, StringComparison.OrdinalIgnoreCase))
                        {
                            Add(entries, file, Path.GetRelativePath(full, file).Replace(Path.DirectorySeparatorChar, '/'), path);
                        }
                    }
                }
                else
                {
                    throw new ActivityFailedException(FileErrorTypes.FileNotFound, $"'{path}' does not exist.");
                }

                break;
            case IReadOnlyList<object?> list:
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in list)
                {
                    var file = options.Files.ResolveExistingFile(item as string ?? throw Invalid(context, "source", "a path or a List of paths (text)"));
                    var name = Path.GetFileName(file);
                    if (!names.Add(name))
                    {
                        throw new ActivityFailedException("InvalidInput", $"'source' of {context.Node.Type} '{context.Node.Id}' has two files named '{name}'; a list is stored by file name.");
                    }

                    Add(entries, file, name, "source");
                }

                break;
            default:
                throw Invalid(context, "source", "a path or a List of paths");
        }

        return entries;
    }

    private void Add(List<(string Full, string Name)> entries, string full, string name, string what)
    {
        if (entries.Count == options.MaxItems)
        {
            throw new ActivityFailedException(FileErrorTypes.TooManyItems, $"'{what}' has more than {options.MaxItems} files (setting maxItems).");
        }

        entries.Add((full, name));
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover partial file is hidden and named as one; the failure that matters is already reported.
        }
    }
}

/// <summary>
/// <c>Zip.Extract</c>: extracts a ZIP archive into a folder (ADR-0043). Every entry is checked before anything is
/// written: an entry leaving the destination (zip slip) is refused, as are more than maxItems files or more than
/// maxExtractBytes in total (zip bombs; the bytes actually written are counted too) and existing files without
/// <c>overwrite</c>. When extraction fails part way, the files it created are removed.
/// </summary>
public sealed class ZipExtractActivity(FilesOptions options) : IActivity
{
    /// <summary>The error type of a file that is not a readable ZIP archive.</summary>
    public const string InvalidArchive = "InvalidArchive";

    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Zip.Extract"),
        "Extract ZIP",
        FilesCategory,
        "Extracts a ZIP archive into a folder inside the file root. Entries that would land outside the folder are refused (FileAccessDenied); more than maxItems files (TooManyItems) or maxExtractBytes in total (FileTooLarge) are refused before anything is written.",
        [
            PathInput("source", "The ZIP file."),
            PathInput("destination", "The folder to extract into (created when missing)."),
            Overwrite(),
            Output(ActivityValueType.List, "Receives the extracted files' paths (a List of String)."),
        ])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sourceText = Text(context, "source");
        var source = ReadableFile(options, sourceText);
        var destinationText = Text(context, "destination");
        var destination = options.Files.Resolve(destinationText);
        if (File.Exists(destination))
        {
            throw new ActivityFailedException(FileErrorTypes.InvalidPath, $"'{destinationText}' is a file, not a folder.");
        }

        var overwrite = Flag(context, "overwrite", false);
        var created = new List<string>();
        var extracted = new List<object?>();
        try
        {
            await Io("extract", sourceText, async () =>
            {
                var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await using (stream.ConfigureAwait(false))
                {
                    ZipArchive archive;
                    try
                    {
                        archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                    }
                    catch (InvalidDataException ex)
                    {
                        throw new ActivityFailedException(InvalidArchive, $"'{sourceText}' is not a ZIP archive this activity reads.", ex);
                    }

                    using (archive)
                    {
                        var plan = Plan(archive, destination, destinationText, overwrite);
                        long written = 0;
                        foreach (var (entry, target) in plan)
                        {
                            context.CancellationToken.ThrowIfCancellationRequested();
                            if (entry is null)
                            {
                                Directory.CreateDirectory(target);
                                continue;
                            }

                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            var existed = File.Exists(target);
                            if (!existed)
                            {
                                created.Add(target);
                            }

                            written = await ExtractAsync(entry, target, written, sourceText, context.CancellationToken).ConfigureAwait(false);
                            extracted.Add(options.Files.Relative(target));
                        }
                    }
                }
            }).ConfigureAwait(false);
        }
        catch
        {
            foreach (var file in created)
            {
                ZipCreateActivity.TryDelete(file);
            }

            throw;
        }

        SetResult(context, WorkflowValues.List(extracted));
        return ActivityResult.Completed;
    }

    /// <summary>Checks every entry and returns where each goes (a null entry is a folder), before anything is written.</summary>
    private List<(ZipArchiveEntry? Entry, string Target)> Plan(ZipArchive archive, string destination, string destinationText, bool overwrite)
    {
        var root = destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var plan = new List<(ZipArchiveEntry?, string)>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declared = 0;
        var files = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(s => s == ".."))
            {
                throw Outside(name, destinationText);
            }

            var target = Path.GetFullPath(Path.Combine(destination, name.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw Outside(name, destinationText);
            }

            // Links between the root and the target are refused like any other path.
            options.Files.Resolve(target);
            if (name.EndsWith('/'))
            {
                plan.Add((null, target));
                continue;
            }

            if (++files > options.MaxItems)
            {
                throw new ActivityFailedException(FileErrorTypes.TooManyItems, $"The archive has more than {options.MaxItems} files (setting maxItems).");
            }

            declared += entry.Length;
            if (declared > options.MaxExtractBytes)
            {
                throw new ActivityFailedException(FileErrorTypes.FileTooLarge, $"The archive holds more than {options.MaxExtractBytes} bytes (setting maxExtractBytes).");
            }

            if (!targets.Add(target))
            {
                throw new ActivityFailedException(InvalidArchive, $"The archive has the entry '{Shown(name)}' twice.");
            }

            if (Directory.Exists(target))
            {
                throw new ActivityFailedException(FileErrorTypes.InvalidPath, $"The archive entry '{Shown(name)}' is a folder in '{destinationText}'.");
            }

            if (File.Exists(target) && !overwrite)
            {
                throw new ActivityFailedException(FileErrorTypes.FileAlreadyExists, $"'{options.Files.Relative(target)}' exists; set 'overwrite' to replace it.");
            }

            plan.Add((entry, target));
        }

        return plan;
    }

    private static ActivityFailedException Outside(string name, string destinationText) =>
        new(FileErrorTypes.FileAccessDenied, $"The archive entry '{Shown(name)}' would be written outside '{destinationText}'; it is refused.");

    /// <summary>Copies one entry, counting the bytes actually written against maxExtractBytes (sizes in the archive can lie).</summary>
    private async Task<long> ExtractAsync(ZipArchiveEntry entry, string target, long written, string sourceText, CancellationToken cancellationToken)
    {
        Stream input;
        try
        {
            input = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new ActivityFailedException(InvalidArchive, $"An entry of '{sourceText}' cannot be read (an unsupported or damaged entry).", ex);
        }

        await using (input.ConfigureAwait(false))
        {
            var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using (output.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    int read;
                    try
                    {
                        read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    }
                    catch (InvalidDataException ex)
                    {
                        throw new ActivityFailedException(InvalidArchive, $"An entry of '{sourceText}' is damaged.", ex);
                    }

                    if (read == 0)
                    {
                        return written;
                    }

                    written += read;
                    if (written > options.MaxExtractBytes)
                    {
                        throw new ActivityFailedException(FileErrorTypes.FileTooLarge, $"The archive expands to more than {options.MaxExtractBytes} bytes (setting maxExtractBytes).");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>An entry name as messages show it (shortened; control characters removed).</summary>
    private static string Shown(string name)
    {
        var clean = new string([.. name.Where(c => !char.IsControl(c))]);
        return clean.Length > 120 ? clean[..120] + "…" : clean;
    }
}
