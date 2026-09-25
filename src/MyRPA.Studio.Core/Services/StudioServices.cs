namespace MyRPA.Studio.Services;

/// <summary>
/// User interaction the view models need from the UI host (ADR-0018). The WPF Studio implements it with windows and
/// message boxes; a web Studio would implement it with dialogs in the browser; tests use a scripted fake.
/// </summary>
public interface IStudioDialogs
{
    /// <summary>Asks for a workflow file to open; null when cancelled.</summary>
    string? ChooseFileToOpen();

    /// <summary>Asks where to save; null when cancelled.</summary>
    /// <param name="suggestedName">Suggested file name.</param>
    string? ChooseFileToSave(string suggestedName);

    /// <summary>Asks what to do with unsaved changes.</summary>
    /// <param name="documentName">The document's name.</param>
    UnsavedChangesChoice AskToSaveChanges(string documentName);

    /// <summary>Asks for input argument values (as text); null when cancelled.</summary>
    /// <param name="arguments">The In and InOut arguments.</param>
    IReadOnlyDictionary<string, string>? AskForArguments(IReadOnlyList<ArgumentPrompt> arguments);

    /// <summary>Asks for a line of text; null when cancelled.</summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="prompt">Question.</param>
    /// <param name="initialValue">Initial text.</param>
    string? AskForText(string title, string prompt, string initialValue);

    /// <summary>Shows an error.</summary>
    /// <param name="title">Title.</param>
    /// <param name="message">Message.</param>
    void ShowError(string title, string message);
}

/// <summary>The answer to "save changes?".</summary>
public enum UnsavedChangesChoice
{
    /// <summary>Save, then continue.</summary>
    Save = 0,

    /// <summary>Continue without saving.</summary>
    Discard = 1,

    /// <summary>Do not continue.</summary>
    Cancel = 2,
}

/// <summary>One argument to ask for before a run.</summary>
/// <param name="Name">Argument name.</param>
/// <param name="Type">Data type name.</param>
/// <param name="Required">Whether a value must be given.</param>
/// <param name="DefaultJson">The default (raw JSON), shown as a hint.</param>
public sealed record ArgumentPrompt(string Name, string Type, bool Required, string? DefaultJson);

/// <summary>Text clipboard.</summary>
public interface IStudioClipboard
{
    /// <summary>Current clipboard text, or null.</summary>
    string? GetText();

    /// <summary>Replaces the clipboard text.</summary>
    /// <param name="text">Text.</param>
    void SetText(string text);
}

/// <summary>Runs an action on the UI thread (run results and logs arrive on background threads).</summary>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> on the UI thread.</summary>
    /// <param name="action">Action.</param>
    void Post(Action action);
}

/// <summary>Reads and writes workflow documents. The desktop Studio uses files; a web Studio would use a repository.</summary>
public interface IWorkflowStorage
{
    /// <summary>Reads a document.</summary>
    /// <param name="location">Where it is (a file path for files).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<string> ReadAsync(string location, CancellationToken cancellationToken);

    /// <summary>Writes a document.</summary>
    /// <param name="location">Where to write.</param>
    /// <param name="json">Workflow JSON.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task WriteAsync(string location, string json, CancellationToken cancellationToken);
}

/// <summary>Workflow documents as files (UTF-8, written through a temporary file so a failed save never truncates).</summary>
public sealed class FileWorkflowStorage : IWorkflowStorage
{
    /// <inheritdoc />
    public Task<string> ReadAsync(string location, CancellationToken cancellationToken) => File.ReadAllTextAsync(location, cancellationToken);

    /// <inheritdoc />
    public async Task WriteAsync(string location, string json, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var temporary = location + ".tmp";
        await File.WriteAllTextAsync(temporary, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, location, overwrite: true);
    }
}
