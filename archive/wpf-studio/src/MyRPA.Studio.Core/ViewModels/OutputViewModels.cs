using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyRPA.Studio.Editing;
using MyRPA.Studio.Running;
using MyRPA.Studio.Services;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;

namespace MyRPA.Studio.ViewModels;

/// <summary>The Output pane: the result of the last run.</summary>
public sealed partial class OutputViewModel : ObservableObject
{
    /// <summary>Status line (e.g. <c>Succeeded</c>, <c>Running…</c>).</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "No run yet.";

    /// <summary>Whether the last run failed, timed out or was cancelled.</summary>
    [ObservableProperty]
    public partial bool IsFailure { get; private set; }

    /// <summary>Run duration.</summary>
    [ObservableProperty]
    public partial string Duration { get; private set; } = string.Empty;

    /// <summary>Execution id of the last run.</summary>
    [ObservableProperty]
    public partial string ExecutionId { get; private set; } = string.Empty;

    /// <summary>Output arguments, one per line (<c>name = value</c>).</summary>
    [ObservableProperty]
    public partial string OutputsText { get; private set; } = string.Empty;

    /// <summary>The error of the last run.</summary>
    [ObservableProperty]
    public partial string ErrorText { get; private set; } = string.Empty;

    /// <summary>A run started.</summary>
    /// <param name="workflowName">Workflow name.</param>
    public void ShowStarted(string workflowName)
    {
        Status = $"Running {workflowName}…";
        IsFailure = false;
        Duration = ExecutionId = OutputsText = ErrorText = string.Empty;
    }

    /// <summary>The workflow could not be started.</summary>
    /// <param name="message">Why.</param>
    public void ShowNotStarted(string message)
    {
        Status = "Not run";
        IsFailure = true;
        Duration = ExecutionId = OutputsText = string.Empty;
        ErrorText = message;
    }

    /// <summary>A run finished.</summary>
    /// <param name="result">Its result.</param>
    public void ShowResult(WorkflowExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Status = result.Status.ToString();
        IsFailure = !result.Succeeded;
        Duration = result.Duration.TotalSeconds.ToString("0.000 's'", CultureInfo.InvariantCulture);
        ExecutionId = result.ExecutionId.ToString();
        OutputsText = string.Join(Environment.NewLine, result.Outputs.Select(o => $"{o.Key} = {WorkflowValues.ToJsonString(o.Value)}"));
        ErrorText = result.Error is { } error
            ? $"{error.Code}: {error.Message}{(error.NodeId is null ? string.Empty : $" (node '{error.NodeId}')")}"
            : string.Empty;
    }
}

/// <summary>One row of the Logs pane.</summary>
/// <param name="Entry">The entry.</param>
public sealed record LogItemViewModel(StudioLogEntry Entry)
{
    /// <summary>Local time.</summary>
    public string Time => Entry.Time.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture);

    /// <summary>Level.</summary>
    public string Level => Entry.Level.ToString();

    /// <summary>Whether the level is Warning or worse.</summary>
    public bool IsProblem => Entry.Level >= LogLevel.Warning;

    /// <summary>Node that wrote it.</summary>
    public string Node => Entry.NodeId ?? string.Empty;

    /// <summary>Message.</summary>
    public string Message => Entry.Message;
}

/// <summary>The Logs pane: workflow and plugin log entries, newest last, capped at <see cref="MaxEntries"/>.</summary>
public sealed partial class LogViewModel : ObservableObject, IDisposable
{
    /// <summary>Entries kept.</summary>
    public const int MaxEntries = 2000;

    private readonly StudioLogFeed _feed;
    private readonly IUiDispatcher _dispatcher;

    /// <summary>Subscribes to the feed.</summary>
    /// <param name="feed">Log entries from the host's logging.</param>
    /// <param name="dispatcher">Moves entries onto the UI thread.</param>
    public LogViewModel(StudioLogFeed feed, IUiDispatcher dispatcher)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _feed.EntryWritten += OnEntryWritten;
    }

    /// <summary>Entries.</summary>
    public ObservableCollection<LogItemViewModel> Entries { get; } = [];

    /// <inheritdoc />
    public void Dispose() => _feed.EntryWritten -= OnEntryWritten;

    /// <summary>Adds an entry (UI thread).</summary>
    /// <param name="entry">The entry.</param>
    public void Add(StudioLogEntry entry)
    {
        Entries.Add(new LogItemViewModel(entry));
        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(0);
        }
    }

    [RelayCommand]
    private void Clear() => Entries.Clear();

    private void OnEntryWritten(object? sender, StudioLogEntry entry) => _dispatcher.Post(() => Add(entry));
}

/// <summary>One row of the Errors pane.</summary>
/// <param name="diagnostic">The diagnostic.</param>
/// <param name="location">Where (node id, argument or variable name, or <c>workflow</c>).</param>
public sealed class ErrorItemViewModel(DraftDiagnostic diagnostic, string location)
{
    /// <summary>The diagnostic.</summary>
    public DraftDiagnostic Diagnostic { get; } = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));

    /// <summary>Error or Warning.</summary>
    public string Severity => Diagnostic.Diagnostic.Severity.ToString();

    /// <summary>Whether it is an error.</summary>
    public bool IsError => Diagnostic.IsError;

    /// <summary>Diagnostic code.</summary>
    public string Code => Diagnostic.Diagnostic.Code;

    /// <summary>Message.</summary>
    public string Message => Diagnostic.Diagnostic.Message;

    /// <summary>Where.</summary>
    public string Location { get; } = location;
}
