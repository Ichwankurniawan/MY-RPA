using MyRPA.Core.Activities;
using MyRPA.Sdk.Files;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;
using static MyRPA.Files.FileValues;

namespace MyRPA.Files;

/// <summary><c>File.Exists</c>: whether a file or folder exists.</summary>
public sealed class FileExistsActivity(FilesOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("File.Exists"),
        "File Exists",
        FilesCategory,
        "Whether a file or folder exists inside the file root. A path outside the root fails (FileAccessDenied) rather than answering false.",
        [PathInput("path", "The file or folder."), Output(ActivityValueType.Boolean, "Receives true or false.")])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var full = options.Files.Resolve(Text(context, "path"));
        SetResult(context, File.Exists(full) || Directory.Exists(full));
        return ActivityResult.CompletedTask;
    }
}

/// <summary><c>File.List</c>: the files (or folders) in a folder.</summary>
public sealed class FileListActivity(FilesOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("File.List"),
        "List Files",
        FilesCategory,
        "Lists the files (or folders) in a folder that match a pattern such as *.csv, sorted, as paths relative to the file root. Links are skipped. Fails with TooManyItems above the plugin's maxItems.",
        [
            PathInput("folder", "The folder (default: the file root).", required: false),
            Input("pattern", ActivityValueType.String, "A name pattern with * and ?.", defaultJson: "\"*\""),
            Input("recursive", ActivityValueType.Boolean, "Also look in sub-folders.", defaultJson: "false"),
            new("kind", ActivityPropertyKind.Text, isRequired: false, "What to list.", ["Files", "Folders", "All"]) { ValueType = ActivityValueType.String, DefaultValue = "\"Files\"" },
            Output(ActivityValueType.List, "Receives the paths (a List of String)."),
        ])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var folderText = OptionalText(context, "folder") ?? ".";
        var folder = options.Files.ResolveExistingFolder(folderText);
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = Flag(context, "recursive", false),
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
            MatchType = MatchType.Simple,
        };
        var pattern = OptionalText(context, "pattern") ?? "*";
        if (pattern.Length == 0 || pattern.Contains('/') || pattern.Contains('\\') || pattern.Contains(".."))
        {
            throw new ActivityFailedException(FileErrorTypes.InvalidPath, "'pattern' is a name pattern such as *.csv; it cannot contain a folder.");
        }

        var entries = context.GetTextOrDefault("kind", "Files") switch
        {
            "Folders" => Directory.EnumerateDirectories(folder, pattern, enumeration),
            "All" => Directory.EnumerateFileSystemEntries(folder, pattern, enumeration),
            _ => Directory.EnumerateFiles(folder, pattern, enumeration),
        };
        var paths = new List<object?>();
        foreach (var entry in entries)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (paths.Count == options.MaxItems)
            {
                throw new ActivityFailedException(FileErrorTypes.TooManyItems, $"'{folderText}' has more than {options.MaxItems} matching entries (setting maxItems); narrow the pattern.");
            }

            paths.Add(options.Files.Relative(entry));
        }

        paths.Sort((a, b) => StringComparer.Ordinal.Compare((string)a!, (string)b!));
        SetResult(context, WorkflowValues.List(paths));
        return ActivityResult.CompletedTask;
    }
}

/// <summary><c>File.ReadText</c>: a text file's content.</summary>
public sealed class FileReadTextActivity(FilesOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("File.ReadText"),
        "Read Text File",
        FilesCategory,
        "Reads a whole text file. Fails with FileNotFound, or FileTooLarge above the plugin's maxFileBytes.",
        [PathInput("path", "The file."), EncodingChoice(), Output(ActivityValueType.String, "Receives the text.")])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = Text(context, "path");
        var full = ReadableFile(options, path);
        var encoding = TextEncoding(context);
        var text = await Io("read", path, async () => await File.ReadAllTextAsync(full, encoding, context.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        SetResult(context, text);
        return ActivityResult.Completed;
    }
}

/// <summary><c>File.WriteText</c>: writes (or appends) text to a file.</summary>
public sealed class FileWriteTextActivity(FilesOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("File.WriteText"),
        "Write Text File",
        FilesCategory,
        "Writes text to a file, creating its folder. An existing file is replaced only with overwrite, or added to with append; otherwise it fails with FileAlreadyExists.",
        [
            PathInput("path", "The file."),
            Input("text", ActivityValueType.String, "The text to write.", required: true),
            Overwrite(),
            Input("append", ActivityValueType.Boolean, "Add to the end of the file (creating it when missing).", defaultJson: "false"),
            EncodingChoice(),
        ])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = Text(context, "path");
        var text = Text(context, "text");
        var append = Flag(context, "append", false);
        var encoding = TextEncoding(context);
        var full = options.Files.ResolveFileToWrite(path, overwrite: append || Flag(context, "overwrite", false));
        await Io("write", path, async () =>
        {
            if (append)
            {
                await File.AppendAllTextAsync(full, text, encoding, context.CancellationToken).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllTextAsync(full, text, encoding, context.CancellationToken).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        return ActivityResult.Completed;
    }
}

/// <summary><c>File.Copy</c>: copies a file.</summary>
public sealed class FileCopyActivity(FilesOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("File.Copy"),
        "Copy File",
        FilesCategory,
        "Copies a file to a new path inside the file root (creating its folder). Copies files only, never folders.",
        [PathInput("source", "The file to copy."), PathInput("destination", "The new file."), Overwrite()])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var source = options.Files.ResolveExistingFile(Text(context, "source"));
        var destinationText = Text(context, "destination");
        var overwrite = Flag(context, "overwrite", false);
        var destination = options.Files.ResolveFileToWrite(destinationText, overwrite);
        await Io("copy to", destinationText, () =>
        {
            File.Copy(source, destination, overwrite);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        return ActivityResult.Completed;
    }
}

/// <summary><c>File.Move</c>: moves or renames a file.</summary>
public sealed class FileMoveActivity(FilesOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("File.Move"),
        "Move File",
        FilesCategory,
        "Moves or renames a file inside the file root (creating the destination's folder). Moves files only, never folders.",
        [PathInput("source", "The file to move."), PathInput("destination", "Its new path."), Overwrite()])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var source = options.Files.ResolveExistingFile(Text(context, "source"));
        var destinationText = Text(context, "destination");
        var overwrite = Flag(context, "overwrite", false);
        var destination = options.Files.ResolveFileToWrite(destinationText, overwrite);
        await Io("move to", destinationText, () =>
        {
            File.Move(source, destination, overwrite);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        return ActivityResult.Completed;
    }
}

/// <summary><c>File.Delete</c>: deletes a file.</summary>
public sealed class FileDeleteActivity(FilesOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("File.Delete"),
        "Delete File",
        FilesCategory,
        "Deletes one file inside the file root. Never deletes folders. A missing file fails (FileNotFound) unless missingOk.",
        [PathInput("path", "The file."), Input("missingOk", ActivityValueType.Boolean, "Succeed when the file does not exist.", defaultJson: "false")])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = Text(context, "path");
        var full = options.Files.Resolve(path);
        if (Directory.Exists(full))
        {
            throw new ActivityFailedException(FileErrorTypes.InvalidPath, $"'{path}' is a folder; File.Delete deletes files only.");
        }

        if (!File.Exists(full))
        {
            return Flag(context, "missingOk", false)
                ? ActivityResult.Completed
                : throw new ActivityFailedException(FileErrorTypes.FileNotFound, $"The file '{path}' does not exist.");
        }

        await Io("delete", path, () =>
        {
            File.Delete(full);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        return ActivityResult.Completed;
    }
}

/// <summary><c>Folder.Create</c>: creates a folder (and its parents).</summary>
public sealed class FolderCreateActivity(FilesOptions options) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Folder.Create"),
        "Create Folder",
        FilesCategory,
        "Creates a folder and any missing parents inside the file root; an existing folder is fine.",
        [PathInput("path", "The folder.")])
    { SideEffects = ActivitySideEffects.FileSystem };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = Text(context, "path");
        var full = options.Files.Resolve(path);
        if (File.Exists(full))
        {
            throw new ActivityFailedException(FileErrorTypes.InvalidPath, $"'{path}' is a file, not a folder.");
        }

        await Io("create", path, () =>
        {
            Directory.CreateDirectory(full);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        return ActivityResult.Completed;
    }
}
