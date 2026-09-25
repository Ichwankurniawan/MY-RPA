using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRPA.Core.Activities;
using MyRPA.Core.Identifiers;
using MyRPA.Studio.Documents;
using MyRPA.Studio.Editing;
using MyRPA.Studio.Running;
using MyRPA.Studio.Services;
using MyRPA.Workflow;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;

namespace MyRPA.Studio.ViewModels;

/// <summary>
/// The Studio window's state and commands (ADR-0018). It edits one workflow document (<see cref="WorkflowDraft"/>
/// with undo/redo), validates it after every change with the engine's own <c>WorkflowLoader</c>, and runs it with the
/// same <see cref="IWorkflowRunner"/> the CLI and Robot use. It is UI-framework neutral: the WPF Studio binds to it and
/// supplies dialogs, clipboard and dispatcher through <see cref="IStudioDialogs"/>, <see cref="IStudioClipboard"/> and
/// <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class StudioViewModel : ObservableObject, IDocumentEditor, IDisposable
{
    private readonly IActivityCatalog _catalog;
    private readonly IWorkflowRunner _runner;
    private readonly IIdGenerator _ids;
    private readonly IStudioDialogs _dialogs;
    private readonly IStudioClipboard _clipboard;
    private readonly IUiDispatcher _dispatcher;
    private readonly IWorkflowStorage _storage;
    private readonly DraftEdits _edits;
    private readonly DraftValidator _validator;
    private readonly Dictionary<string, NodeRunState> _runStates = new(StringComparer.Ordinal);
    private DocumentHistory _history;
    private NodePath? _selected;
    private CancellationTokenSource? _runCancellation;

    /// <summary>Creates the Studio view model with a new, empty workflow.</summary>
    /// <param name="catalog">Registered activities (built-in and plugins).</param>
    /// <param name="runner">Runs workflows.</param>
    /// <param name="ids">Correlation ids for runs.</param>
    /// <param name="dialogs">User interaction.</param>
    /// <param name="clipboard">Clipboard.</param>
    /// <param name="dispatcher">UI thread.</param>
    /// <param name="storage">Reads and writes documents.</param>
    /// <param name="logFeed">Log entries for the Logs pane.</param>
    public StudioViewModel(
        IActivityCatalog catalog,
        IWorkflowRunner runner,
        IIdGenerator ids,
        IStudioDialogs dialogs,
        IStudioClipboard clipboard,
        IUiDispatcher dispatcher,
        IWorkflowStorage storage,
        StudioLogFeed logFeed)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _edits = new DraftEdits(catalog);
        _validator = new DraftValidator(catalog);
        Toolbox = new ToolboxViewModel(catalog);
        Properties = new PropertiesViewModel(this);
        Variables = new VariablesViewModel(this);
        Arguments = new ArgumentsViewModel(this);
        Log = new LogViewModel(logFeed, dispatcher);
        _history = new DocumentHistory(WorkflowDraft.CreateNew());
        _history.Changed += OnHistoryChanged;
        Validation = _validator.Validate(_history.Current);
        Root = NodeViewModel.Build(_history.Current, catalog);
        Refresh();
    }

    /// <summary>The Activities pane.</summary>
    public ToolboxViewModel Toolbox { get; }

    /// <summary>The Properties pane.</summary>
    public PropertiesViewModel Properties { get; }

    /// <summary>The Variables pane.</summary>
    public VariablesViewModel Variables { get; }

    /// <summary>The Arguments pane.</summary>
    public ArgumentsViewModel Arguments { get; }

    /// <summary>The Output pane.</summary>
    public OutputViewModel Output { get; } = new();

    /// <summary>The Logs pane.</summary>
    public LogViewModel Log { get; }

    /// <summary>The Errors pane.</summary>
    public ObservableCollection<ErrorItemViewModel> Errors { get; } = [];

    /// <summary>Data types for the Variables and Arguments grids.</summary>
    public static IReadOnlyList<string> DataTypes => DataChoices.Types;

    /// <summary>Argument directions.</summary>
    public static IReadOnlyList<string> Directions => DataChoices.Directions;

    /// <inheritdoc />
    public WorkflowDraft Document => _history.Current;

    /// <summary>The designer's root block (rebuilt after every change).</summary>
    [ObservableProperty]
    public partial NodeViewModel Root { get; private set; }

    /// <summary>The selected block (null: the workflow itself).</summary>
    [ObservableProperty]
    public partial NodeViewModel? SelectedNode { get; private set; }

    /// <summary>The last validation of the document.</summary>
    [ObservableProperty]
    public partial DraftValidation Validation { get; private set; }

    /// <summary>The document's file (null until saved).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DocumentName))]
    public partial string? FilePath { get; private set; }

    /// <summary>File name, or <c>Untitled</c>.</summary>
    public string DocumentName => FilePath is null ? "Untitled" : Path.GetFileName(FilePath);

    /// <summary>Window title (with <c>*</c> when there are unsaved changes).</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    /// <summary>Whether the document has unsaved changes.</summary>
    public bool IsDirty => _history.IsDirty;

    /// <summary>Status bar text (validation summary).</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    /// <summary>Whether a run is in progress.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsRunning { get; private set; }

    /// <summary>Optional run timeout (none by default).</summary>
    [ObservableProperty]
    public partial TimeSpan? RunTimeout { get; set; }

    /// <summary>The Edit menu's undo text.</summary>
    public string UndoText => _history.UndoDescription is { } d ? $"Undo {d}" : "Undo";

    /// <summary>The Edit menu's redo text.</summary>
    public string RedoText => _history.RedoDescription is { } d ? $"Redo {d}" : "Redo";

    /// <inheritdoc />
    public bool Edit(Func<WorkflowDraft, WorkflowDraft> edit, string description)
    {
        ArgumentNullException.ThrowIfNull(edit);
        WorkflowDraft next;
        try
        {
            next = edit(_history.Current);
        }
        catch (EditException ex)
        {
            _dialogs.ShowError("Cannot make this change", ex.Message);
            return false;
        }

        return _history.Apply(next, description);
    }

    /// <summary>
    /// Applies values typed into an editor but not committed yet (editors commit on Enter or when focus leaves them, and
    /// a keyboard shortcut or closing the window does neither), as one undoable edit. Save, Run and the unsaved-changes
    /// check call it first, so they always see what the user typed.
    /// </summary>
    /// <returns>False when a pending value was refused (the error has been shown and the value is kept for fixing).</returns>
    public bool CommitPendingEdits()
    {
        var edits = new[] { Properties.PendingEdit(), Variables.PendingEdit(), Arguments.PendingEdit() }.OfType<Func<WorkflowDraft, WorkflowDraft>>().ToList();
        if (edits.Count == 0)
        {
            return true;
        }

        WorkflowDraft next;
        try
        {
            next = edits.Aggregate(_history.Current, (draft, edit) => edit(draft));
        }
        catch (EditException ex)
        {
            _dialogs.ShowError("Cannot apply the value being edited", ex.Message);
            return false;
        }

        _history.Apply(next, "Edit");
        return true;
    }

    /// <summary>Selects a block (null selects the workflow).</summary>
    /// <param name="node">The block.</param>
    public void Select(NodeViewModel? node) => SelectPath(node?.Path);

    /// <summary>Selects the node at <paramref name="path"/> (null or unknown: the workflow).</summary>
    /// <param name="path">The node's path.</param>
    public void SelectPath(NodePath? path)
    {
        _selected = path is not null && DraftTree.Find(_history.Current, path) is not null ? path : null;
        var selected = _selected is null ? null : Root.DescendantsAndSelf().FirstOrDefault(n => n.Path == _selected);
        foreach (var node in Root.DescendantsAndSelf())
        {
            node.IsSelected = ReferenceEquals(node, selected);
        }

        SelectedNode = selected;
        ShowProperties();
        NotifySelectionCommands();
    }

    /// <summary>Shows the workflow's own properties (deselects any block).</summary>
    [RelayCommand]
    public void SelectWorkflow() => SelectPath(null);

    /// <summary>Selects the location of an item of the Errors pane.</summary>
    /// <param name="item">The error.</param>
    [RelayCommand]
    public void GoToError(ErrorItemViewModel? item)
    {
        if (item is not null)
        {
            SelectPath(item.Diagnostic.Node);
        }
    }

    /// <summary>Whether <paramref name="payload"/> may be dropped on <paramref name="zone"/>.</summary>
    /// <param name="payload">What is dragged.</param>
    /// <param name="zone">Where.</param>
    public bool CanDrop(DragPayload? payload, DropZoneViewModel? zone)
    {
        if (payload is null || zone is null)
        {
            return false;
        }

        // A case slot is named when dropped; test the drop with a placeholder name.
        var position = zone.NeedsSlotName && zone.Position is SlotPosition prefix ? new SlotPosition(prefix.Name + "?") : zone.Position;
        return payload switch
        {
            NewActivityPayload => _edits.CanInsert(_history.Current, zone.ParentPath, position, out _),
            MoveNodePayload move => !move.Path.IsRoot && !move.Path.IsAncestorOrSelfOf(zone.ParentPath)
                && !IsSamePlace(move.Path, zone) && _edits.CanInsert(_history.Current, zone.ParentPath, position, out _),
            _ => false,
        };
    }

    /// <summary>Drops <paramref name="payload"/> on <paramref name="zone"/>: inserts a new activity or moves a block.</summary>
    /// <param name="payload">What is dragged.</param>
    /// <param name="zone">Where.</param>
    /// <returns>Whether the document changed.</returns>
    public bool Drop(DragPayload? payload, DropZoneViewModel? zone)
    {
        if (!CanDrop(payload, zone))
        {
            return false;
        }

        var position = zone!.Position;
        if (zone.NeedsSlotName && position is SlotPosition prefix)
        {
            var name = _dialogs.AskForText("Add case", "Value of the new case:", string.Empty)?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            position = new SlotPosition(prefix.Name + name);
        }

        NodePath? placed = null;
        var changed = payload switch
        {
            NewActivityPayload add when Descriptor(add.TypeName.Value) is { } descriptor => Edit(
                d =>
                {
                    var result = _edits.Insert(d, zone.ParentPath, position, DraftEdits.CreateNode(d, descriptor));
                    placed = result.Path;
                    return result.Draft;
                },
                $"Add {descriptor.DisplayName}"),
            MoveNodePayload move => Edit(
                d =>
                {
                    var result = _edits.Move(d, move.Path, zone.ParentPath, position);
                    placed = result.Path;
                    return result.Draft;
                },
                "Move activity"),
            _ => false,
        };

        if (changed)
        {
            SelectPath(placed);
        }

        return changed;
    }

    /// <summary>Opens a workflow file (used by the Open command, the command line and file drops).</summary>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether it was opened.</returns>
    public async Task<bool> OpenFileAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        WorkflowDraft draft;
        try
        {
            var json = await _storage.ReadAsync(path, cancellationToken).ConfigureAwait(true);
            draft = DraftJson.Read(json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DraftReadException)
        {
            _dialogs.ShowError("Cannot open workflow", $"{path}{Environment.NewLine}{ex.Message}");
            return false;
        }

        FilePath = Path.GetFullPath(path);
        ReplaceDocument(draft);
        return true;
    }

    /// <summary>Asks to save unsaved changes before the document is closed.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether closing may continue.</returns>
    public async Task<bool> ConfirmCloseAsync(CancellationToken cancellationToken)
    {
        if (!CommitPendingEdits())
        {
            return false;
        }

        if (!_history.IsDirty)
        {
            return true;
        }

        return _dialogs.AskToSaveChanges(DocumentName) switch
        {
            UnsavedChangesChoice.Save => await SaveCoreAsync(saveAs: false, cancellationToken).ConfigureAwait(true),
            UnsavedChangesChoice.Discard => true,
            _ => false,
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _history.Changed -= OnHistoryChanged;
        _runCancellation?.Cancel();
        Log.Dispose();
    }

    [RelayCommand]
    private async Task NewAsync(CancellationToken cancellationToken)
    {
        if (await ConfirmCloseAsync(cancellationToken).ConfigureAwait(true))
        {
            FilePath = null;
            ReplaceDocument(WorkflowDraft.CreateNew());
        }
    }

    [RelayCommand]
    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (await ConfirmCloseAsync(cancellationToken).ConfigureAwait(true) && _dialogs.ChooseFileToOpen() is { } path)
        {
            await OpenFileAsync(path, cancellationToken).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken) => await SaveCoreAsync(saveAs: false, cancellationToken).ConfigureAwait(true);

    [RelayCommand]
    private async Task SaveAsAsync(CancellationToken cancellationToken) => await SaveCoreAsync(saveAs: true, cancellationToken).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => _history.Undo();

    private bool CanUndo() => _history.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => _history.Redo();

    private bool CanRedo() => _history.CanRedo;

    [RelayCommand(CanExecute = nameof(HasRemovableSelection))]
    private void Copy()
    {
        if (_selected is not null && DraftTree.Find(_history.Current, _selected) is { } node)
        {
            _clipboard.SetText(DraftClipboard.Serialize([node]));
        }
    }

    [RelayCommand(CanExecute = nameof(HasRemovableSelection))]
    private void Cut()
    {
        Copy();
        Delete();
    }

    [RelayCommand]
    private void Paste()
    {
        if (!DraftClipboard.TryDeserialize(_clipboard.GetText(), out var nodes) || nodes.Count == 0)
        {
            return;
        }

        if (InsertionPoint() is not (var parent, var index))
        {
            _dialogs.ShowError("Cannot paste", "Select a block that contains a list of activities, or an activity in such a list.");
            return;
        }

        NodePath? last = null;
        if (Edit(
                d =>
                {
                    var prepared = DraftClipboard.PrepareForPaste(nodes, d);
                    for (var i = 0; i < prepared.Count; i++)
                    {
                        (d, last) = _edits.Insert(d, parent, new ChildPosition(index + i), prepared[i]);
                    }

                    return d;
                },
                nodes.Count == 1 ? "Paste activity" : $"Paste {nodes.Count} activities"))
        {
            SelectPath(last);
        }
    }

    [RelayCommand(CanExecute = nameof(HasRemovableSelection))]
    private void Delete()
    {
        if (_selected is { IsRoot: false } path && Edit(d => DraftEdits.Remove(d, path), "Delete activity"))
        {
            SelectPath(path.Parent);
        }
    }

    private bool HasRemovableSelection() => _selected is { IsRoot: false };

    /// <summary>Adds an activity after the selected one (or into the selected container), e.g. on toolbox double-click.</summary>
    [RelayCommand]
    private void AddActivity(string? typeName)
    {
        if (typeName is null || Descriptor(typeName) is not { } descriptor)
        {
            return;
        }

        if (InsertionPoint() is not (var parent, var index))
        {
            _dialogs.ShowError("Cannot add activity", "Select a block that contains a list of activities, or drag the activity onto a drop zone.");
            return;
        }

        NodePath? placed = null;
        if (Edit(
                d =>
                {
                    (d, placed) = _edits.Insert(d, parent, new ChildPosition(index), DraftEdits.CreateNode(d, descriptor));
                    return d;
                },
                $"Add {descriptor.DisplayName}"))
        {
            SelectPath(placed);
        }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (!CommitPendingEdits())
        {
            return;
        }

        var validation = _validator.Validate(_history.Current);
        if (validation.Workflow is not { } workflow)
        {
            var errors = validation.Diagnostics.Count(d => d.IsError);
            Output.ShowNotStarted($"The workflow has {errors} error(s). Fix them (see Errors) and run again.");
            return;
        }

        if (CollectArguments(workflow) is not { } arguments)
        {
            return;
        }

        ClearRunStates();
        Output.ShowStarted(workflow.Name);
        var correlationId = _ids.NewCorrelationId();
        using var cancellation = new CancellationTokenSource();
        using var monitor = new RunMonitor(correlationId.Value);
        monitor.NodeStarted += (_, nodeId) => _dispatcher.Post(() => SetRunState(nodeId, NodeRunState.Running));
        monitor.NodeStopped += (_, nodeId) => _dispatcher.Post(() => SetRunState(nodeId, NodeRunState.Completed));
        _runCancellation = cancellation;
        IsRunning = true;
        try
        {
            var request = new WorkflowRunRequest
            {
                Arguments = arguments,
                CorrelationId = correlationId,
                Timeout = RunTimeout,
                Location = FilePath,
            };
            var result = await _runner.RunAsync(workflow, request, cancellation.Token).ConfigureAwait(true);

            // Posted, so it is applied after the node events queued before it.
            _dispatcher.Post(() => ShowResult(result));
        }
#pragma warning disable CA1031 // The runner reports workflow failures in its result; anything else is a host fault, shown instead of crashing Studio.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _dispatcher.Post(() => Output.ShowNotStarted($"The run could not be completed: {ex.Message}"));
        }
        finally
        {
            _runCancellation = null;
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _runCancellation?.Cancel();

    private Dictionary<string, object?>? CollectArguments(WorkflowDefinition workflow)
    {
        var inputs = workflow.Arguments.Where(a => a.IsInput).ToList();
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (inputs.Count == 0)
        {
            return values;
        }

        var answers = _dialogs.AskForArguments(
            [.. inputs.Select(a => new ArgumentPrompt(
                a.Name,
                a.Type.ToString(),
                a.IsRequired,
                _history.Current.Arguments.FirstOrDefault(d => d.Name == a.Name)?.DefaultJson))]);
        if (answers is null)
        {
            return null;
        }

        foreach (var argument in inputs)
        {
            // A blank answer leaves the argument unset, so the engine applies its default (or reports it missing).
            if (!answers.TryGetValue(argument.Name, out var text) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!WorkflowValues.TryParseText(text, argument.Type, out var value, out var error))
            {
                Output.ShowNotStarted($"Argument '{argument.Name}': {error}");
                return null;
            }

            values[argument.Name] = value;
        }

        return values;
    }

    private void ShowResult(WorkflowExecutionResult result)
    {
        Output.ShowResult(result);
        if (result.Error?.NodeId is { } failed)
        {
            SetRunState(failed, NodeRunState.Failed);
        }
    }

    private void ClearRunStates()
    {
        _runStates.Clear();
        foreach (var node in Root.DescendantsAndSelf())
        {
            node.RunState = NodeRunState.None;
        }
    }

    private void SetRunState(string nodeId, NodeRunState state)
    {
        // A failure is final; a later "stopped" event for the same node must not hide it.
        if (_runStates.TryGetValue(nodeId, out var current) && current == NodeRunState.Failed)
        {
            return;
        }

        _runStates[nodeId] = state;
        foreach (var node in Root.DescendantsAndSelf().Where(n => n.Id == nodeId))
        {
            node.RunState = state;
        }
    }

    private async Task<bool> SaveCoreAsync(bool saveAs, CancellationToken cancellationToken)
    {
        if (!CommitPendingEdits())
        {
            return false;
        }

        var path = FilePath;
        if (saveAs || path is null)
        {
            path = _dialogs.ChooseFileToSave(FilePath is null ? $"{_history.Current.Id}.json" : Path.GetFileName(FilePath));
            if (path is null)
            {
                return false;
            }
        }

        var saved = _history.Current;
        try
        {
            await _storage.WriteAsync(path, DraftJson.Write(saved), cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Cannot save workflow", $"{path}{Environment.NewLine}{ex.Message}");
            return false;
        }

        FilePath = Path.GetFullPath(path);

        // Only mark saved if nothing changed while writing.
        if (ReferenceEquals(saved, _history.Current))
        {
            _history.MarkSaved();
        }

        UpdateTitle();
        return true;
    }

    private void ReplaceDocument(WorkflowDraft draft)
    {
        _history.Changed -= OnHistoryChanged;
        _history = new DocumentHistory(draft);
        _history.Changed += OnHistoryChanged;
        _selected = null;
        _runStates.Clear();
        Refresh();
    }

    private void OnHistoryChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var draft = _history.Current;
        Validation = _validator.Validate(draft);
        Root = NodeViewModel.Build(draft, _catalog);

        var byNode = Validation.Diagnostics.Where(d => d.Node is not null).ToLookup(d => d.Node!);
        foreach (var node in Root.DescendantsAndSelf())
        {
            var diagnostics = byNode[node.Path].ToList();
            node.HasErrors = diagnostics.Any(d => d.IsError);
            node.ErrorText = string.Join(Environment.NewLine, diagnostics.Select(d => d.Diagnostic.Message));
            node.RunState = _runStates.GetValueOrDefault(node.Id);
        }

        Errors.Clear();
        foreach (var diagnostic in Validation.Diagnostics)
        {
            Errors.Add(new ErrorItemViewModel(diagnostic, Location(draft, diagnostic)));
        }

        Variables.Load(draft, Validation.Diagnostics);
        Arguments.Load(draft, Validation.Diagnostics);
        var errorCount = Validation.Diagnostics.Count(d => d.IsError);
        var warningCount = Validation.Diagnostics.Count - errorCount;
        StatusText = errorCount == 0 && warningCount == 0 ? "Valid" : $"{errorCount} error(s), {warningCount} warning(s)";

        SelectPath(_selected);
        UpdateTitle();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(RedoText));
        OnPropertyChanged(nameof(Document));
    }

    private void ShowProperties()
    {
        var draft = _history.Current;
        if (_selected is null)
        {
            Properties.ShowWorkflow(draft, Validation.Diagnostics.Where(d => d.Node is null && d.ArgumentIndex is null && d.VariableIndex is null));
        }
        else
        {
            var node = DraftTree.Get(draft, _selected);
            Properties.ShowNode(draft, _selected, Descriptor(node.Type), [.. Validation.Diagnostics.Where(d => d.Node == _selected)]);
        }
    }

    private void NotifySelectionCommands()
    {
        CopyCommand.NotifyCanExecuteChanged();
        CutCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    private void UpdateTitle()
    {
        Title = $"{DocumentName}{(_history.IsDirty ? " *" : string.Empty)} — MyRPA Studio";
        OnPropertyChanged(nameof(IsDirty));
    }

    private ActivityDescriptor? Descriptor(string typeName) =>
        ActivityTypeName.TryCreate(typeName, out var type) && _catalog.TryGet(type, out var descriptor) ? descriptor : null;

    // Where a pasted or added activity goes: after the selected activity in its list, or at the end of the selected
    // container (the root when nothing is selected).
    private (NodePath Parent, int Index)? InsertionPoint()
    {
        var draft = _history.Current;
        var target = _selected ?? NodePath.Root;
        var node = DraftTree.Get(draft, target);
        if (Descriptor(node.Type)?.AllowsChildren == true)
        {
            return (target, node.Children.Count);
        }

        return target is { Parent: { } parent, Last: ChildStep step } ? (parent, step.Index + 1) : null;
    }

    private static bool IsSamePlace(NodePath moved, DropZoneViewModel zone) =>
        moved.Parent == zone.ParentPath && moved.Last is ChildStep step && zone.Position is ChildPosition position
        && (position.Index == step.Index || position.Index == step.Index + 1);

    private static string Location(WorkflowDraft draft, DraftDiagnostic diagnostic)
    {
        if (diagnostic.Node is { } path && DraftTree.Find(draft, path) is { } node)
        {
            return diagnostic.Property is null ? node.Id : $"{node.Id}.{diagnostic.Property}";
        }

        if (diagnostic.ArgumentIndex is { } a && a < draft.Arguments.Count)
        {
            return $"argument {draft.Arguments[a].Name}";
        }

        return diagnostic.VariableIndex is { } v && v < draft.Variables.Count ? $"variable {draft.Variables[v].Name}" : "workflow";
    }
}
