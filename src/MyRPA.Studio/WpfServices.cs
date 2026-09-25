using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using MyRPA.Studio.Dialogs;
using MyRPA.Studio.Services;

namespace MyRPA.Studio;

/// <summary>Posts to the WPF dispatcher of the thread that created it (the UI thread).</summary>
internal sealed class WpfDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }
}

/// <summary>The Windows clipboard (text only).</summary>
internal sealed class WpfClipboard : IStudioClipboard
{
    /// <inheritdoc />
    public string? GetText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (ExternalException)
        {
            // Another application holds the clipboard open; pasting nothing is the expected outcome.
            return null;
        }
    }

    /// <inheritdoc />
    public void SetText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException ex)
        {
            MessageBox.Show($"The clipboard is in use by another application.\n{ex.Message}", "Copy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

/// <summary>File dialogs, message boxes and Studio's own dialogs, owned by the main window.</summary>
internal sealed class WpfDialogs : IStudioDialogs
{
    private const string Filter = "MyRPA workflows (*.json)|*.json|All files (*.*)|*.*";

    private static Window? Owner => Application.Current?.MainWindow is { IsLoaded: true } window ? window : null;

    /// <inheritdoc />
    public string? ChooseFileToOpen()
    {
        var dialog = new OpenFileDialog { Filter = Filter, Title = "Open workflow", CheckFileExists = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public string? ChooseFileToSave(string suggestedName)
    {
        var dialog = new SaveFileDialog { Filter = Filter, Title = "Save workflow", FileName = suggestedName, AddExtension = true, DefaultExt = ".json", OverwritePrompt = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    /// <inheritdoc />
    public UnsavedChangesChoice AskToSaveChanges(string documentName) =>
        Show($"Save changes to {documentName}?", "MyRPA Studio", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => UnsavedChangesChoice.Save,
            MessageBoxResult.No => UnsavedChangesChoice.Discard,
            _ => UnsavedChangesChoice.Cancel,
        };

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string>? AskForArguments(IReadOnlyList<ArgumentPrompt> arguments)
    {
        var dialog = new ArgumentsDialog(arguments) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Values : null;
    }

    /// <inheritdoc />
    public string? AskForText(string title, string prompt, string initialValue)
    {
        var dialog = new TextPromptDialog(title, prompt, initialValue) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    /// <inheritdoc />
    public void ShowError(string title, string message) => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image) =>
        Owner is { } owner ? MessageBox.Show(owner, message, title, buttons, image) : MessageBox.Show(message, title, buttons, image);
}
