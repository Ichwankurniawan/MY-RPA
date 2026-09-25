using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MyRPA.Studio.ViewModels;

namespace MyRPA.Studio;

/// <summary>
/// The Studio window (PRD 5.2): Activities | Designer | Properties, with Variables, Arguments, Output, Logs and Errors
/// below. Everything is bound to <see cref="StudioViewModel"/>; this code only routes a few window-level events.
/// </summary>
internal sealed partial class MainWindow : Window
{
    private readonly StudioViewModel _studio;
    private bool _closeConfirmed;
    private bool _confirming;

    public MainWindow(StudioViewModel studio)
    {
        _studio = studio ?? throw new ArgumentNullException(nameof(studio));
        DataContext = studio;
        InitializeComponent();
        studio.PropertyChanged += OnStudioPropertyChanged;
    }

    /// <summary>The view model.</summary>
    public StudioViewModel Studio => _studio;

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnClosing(e);
        if (_closeConfirmed)
        {
            return;
        }

        // Closing waits for "save changes?" (which may save asynchronously), so it is cancelled here and repeated
        // once the answer is known.
        e.Cancel = true;
        if (!_confirming)
        {
            _ = ConfirmAndCloseAsync();
        }
    }

    private async Task ConfirmAndCloseAsync()
    {
        _confirming = true;
        try
        {
            if (_studio.IsRunning)
            {
                _studio.StopCommand.Execute(null);
            }

            if (await _studio.ConfirmCloseAsync(CancellationToken.None).ConfigureAwait(true))
            {
                _closeConfirmed = true;

                // The answer may arrive while the cancelled Closing event is still on the stack (no save was needed),
                // and WPF refuses Close() from inside Closing; close once it has returned.
                _ = Dispatcher.BeginInvoke(Close);
            }
        }
        finally
        {
            _confirming = false;
        }
    }

    private void OnStudioPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Show the result of a run where it matters: errors when it could not start, output otherwise.
        if (e.PropertyName == nameof(StudioViewModel.IsRunning) && _studio.IsRunning)
        {
            OutputTab.IsSelected = true;
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnShowErrorsClick(object sender, RoutedEventArgs e) => ErrorsTab.IsSelected = true;

    private void OnShowLogsClick(object sender, RoutedEventArgs e) => LogsTab.IsSelected = true;

    private void OnDesignerBackgroundClick(object sender, MouseButtonEventArgs e)
    {
        // Blocks handle their own clicks; a click on the empty canvas shows the workflow's properties.
        _studio.SelectWorkflow();
        DesignerSurface.Focus();
    }

    private void OnErrorDoubleClick(object sender, MouseButtonEventArgs e) => GoToError(sender);

    private void OnErrorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            GoToError(sender);
            e.Handled = true;
        }
    }

    private void GoToError(object sender)
    {
        if (sender is ListViewItem { DataContext: ErrorItemViewModel item })
        {
            _studio.GoToErrorCommand.Execute(item);
        }
    }
}
