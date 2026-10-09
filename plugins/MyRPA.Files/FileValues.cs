using System.Text;
using MyRPA.Core.Activities;
using MyRPA.Sdk.Files;
using MyRPA.Workflow.Execution;

namespace MyRPA.Files;

/// <summary>Property declarations and checks shared by the files plugin's activities.</summary>
internal static class FileValues
{
    public const string FilesCategory = "Files";

    public const string DataFilesCategory = "Data Files";

    public static ActivityPropertyDefinition Input(string name, ActivityValueType type, string description, bool required = false, string? defaultJson = null) =>
        new(name, ActivityPropertyKind.Expression, required, description) { ValueType = type, DefaultValue = defaultJson };

    public static ActivityPropertyDefinition PathInput(string name, string description, bool required = true) =>
        Input(name, ActivityValueType.String, description + " Relative to the plugin's file root (or absolute inside it).", required);

    public static ActivityPropertyDefinition Output(ActivityValueType type, string description) =>
        new("result", ActivityPropertyKind.AssignmentTarget, isRequired: true, description) { ValueType = type };

    public static ActivityPropertyDefinition EncodingChoice() =>
        new("encoding", ActivityPropertyKind.Text, isRequired: false, "Text encoding.", ["utf-8", "utf-16", "latin1", "ascii"])
        { ValueType = ActivityValueType.String, DefaultValue = "\"utf-8\"" };

    public static ActivityPropertyDefinition Overwrite() =>
        Input("overwrite", ActivityValueType.Boolean, "Replace the file when it exists (otherwise that fails with FileAlreadyExists).", defaultJson: "false");

    public static string Text(IActivityContext context, string name) =>
        context.Evaluate(name) is string text ? text : throw Invalid(context, name, "text");

    public static string? OptionalText(IActivityContext context, string name) => context.HasProperty(name) ? Text(context, name) : null;

    public static bool Flag(IActivityContext context, string name, bool defaultValue) =>
        !context.HasProperty(name) ? defaultValue : context.Evaluate(name) is bool b ? b : throw Invalid(context, name, "true or false");

    public static void SetResult(IActivityContext context, object? value) => context.SetValue(context.GetName("result"), value);

    public static Encoding TextEncoding(IActivityContext context) => context.GetTextOrDefault("encoding", "utf-8") switch
    {
        "utf-16" => Encoding.Unicode,
        "latin1" => Encoding.Latin1,
        "ascii" => Encoding.ASCII,
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    };

    /// <summary>A value of the wrong kind: says which property and what it must be, never the value itself.</summary>
    public static ActivityFailedException Invalid(IActivityContext context, string name, string expected) =>
        new("InvalidInput", $"'{name}' of {context.Node.Type} '{context.Node.Id}' must be {expected}.");

    /// <summary>An existing file that is not larger than the limit.</summary>
    public static string ReadableFile(FilesOptions options, string path)
    {
        var full = options.Files.ResolveExistingFile(path);
        return new FileInfo(full).Length <= options.MaxFileBytes
            ? full
            : throw new ActivityFailedException(FileErrorTypes.FileTooLarge, $"The file '{path}' is larger than the limit of {options.MaxFileBytes} bytes (setting maxFileBytes).");
    }

    /// <summary>
    /// Runs an I/O operation. An operating-system failure becomes <see cref="FileErrorTypes.FileIoError"/> (or
    /// <see cref="FileErrorTypes.FileNotFound"/>) with a message that does not repeat the system's text, which can
    /// contain absolute paths; the cause stays the inner exception.
    /// </summary>
    public static async ValueTask<T> Io<T>(string what, string path, Func<ValueTask<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ActivityFailedException(FileErrorTypes.FileNotFound, $"Cannot {what} '{path}': it does not exist (any more).", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ActivityFailedException(FileErrorTypes.FileIoError, $"Cannot {what} '{path}': it is in use, read-only or not accessible.", ex);
        }
    }

    /// <summary><see cref="Io{T}"/> for an operation without a value.</summary>
    public static async ValueTask Io(string what, string path, Func<ValueTask> operation) =>
        await Io(what, path, async () =>
        {
            await operation().ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
}
