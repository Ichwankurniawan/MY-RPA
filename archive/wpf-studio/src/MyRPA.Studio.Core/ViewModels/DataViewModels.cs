using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRPA.Studio.Documents;
using MyRPA.Studio.Editing;
using MyRPA.Workflow.Values;

namespace MyRPA.Studio.ViewModels;

/// <summary>Choices shared by the Variables and Arguments grids.</summary>
public static class DataChoices
{
    /// <summary>Workflow data types.</summary>
    public static IReadOnlyList<string> Types { get; } = Enum.GetNames<WorkflowDataType>();

    /// <summary>Argument directions.</summary>
    public static IReadOnlyList<string> Directions { get; } = ["In", "Out", "InOut"];

    /// <summary>A name like <c>variable1</c> that is not in <paramref name="used"/>.</summary>
    /// <param name="baseName">Base name.</param>
    /// <param name="used">Names in use.</param>
    internal static string UniqueName(string baseName, IEnumerable<string> used)
    {
        var set = used.ToHashSet(StringComparer.Ordinal);
        for (var i = 1; ; i++)
        {
            var name = baseName + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!set.Contains(name))
            {
                return name;
            }
        }
    }

    internal static string? DefaultOrNull(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    internal static string Messages(IEnumerable<DraftDiagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(d => d.Diagnostic.Message));
}

/// <summary>The Variables pane: a grid of the workflow's variables (name, type, default as JSON).</summary>
/// <param name="editor">Applies edits.</param>
public sealed partial class VariablesViewModel(IDocumentEditor editor) : ObservableObject
{
    private readonly IDocumentEditor _editor = editor ?? throw new ArgumentNullException(nameof(editor));

    /// <summary>Rows.</summary>
    public ObservableCollection<VariableRowViewModel> Rows { get; } = [];

    /// <summary>Shows the document's variables (rows are reused when the count is unchanged, to keep focus).</summary>
    /// <param name="draft">The document.</param>
    /// <param name="diagnostics">All diagnostics (those about variables are shown on their rows).</param>
    public void Load(WorkflowDraft draft, IReadOnlyList<DraftDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (Rows.Count != draft.Variables.Count)
        {
            Rows.Clear();
            for (var i = 0; i < draft.Variables.Count; i++)
            {
                Rows.Add(new VariableRowViewModel(this, i));
            }
        }

        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i].Update(draft.Variables[i], DataChoices.Messages(diagnostics.Where(d => d.VariableIndex == i)));
        }
    }

    /// <summary>The edit that would apply rows typed into but not committed yet; null when nothing is pending.</summary>
    internal Func<WorkflowDraft, WorkflowDraft>? PendingEdit()
    {
        var current = _editor.Document.Variables;
        var pending = Rows.Where(r => r.Index < current.Count).Select(r => (r.Index, Next: Draft(r))).Where(p => p.Next != current[p.Index]).ToList();
        return pending.Count == 0 ? null : d => pending.Aggregate(d, (acc, p) => DraftEdits.SetVariable(acc, p.Index, p.Next));
    }

    internal void Commit(VariableRowViewModel row)
    {
        var next = Draft(row);
        if (next != _editor.Document.Variables[row.Index] && !_editor.Edit(d => DraftEdits.SetVariable(d, row.Index, next), $"Edit variable {next.Name}"))
        {
            row.Update(_editor.Document.Variables[row.Index], row.ErrorText);
        }
    }

    private static VariableDraft Draft(VariableRowViewModel row) =>
        new(row.Name.Trim(), row.Type, DataChoices.DefaultOrNull(row.DefaultJson));

    [RelayCommand]
    private void Add()
    {
        var name = DataChoices.UniqueName("variable", _editor.Document.Variables.Select(v => v.Name).Concat(_editor.Document.Arguments.Select(a => a.Name)));
        _editor.Edit(d => DraftEdits.SetVariable(d, null, new VariableDraft(name, nameof(WorkflowDataType.String))), $"Add variable {name}");
    }

    [RelayCommand]
    private void Remove(VariableRowViewModel? row)
    {
        if (row is not null)
        {
            _editor.Edit(d => DraftEdits.RemoveVariable(d, row.Index), $"Remove variable {row.Name}");
        }
    }
}

/// <summary>One variable.</summary>
public sealed partial class VariableRowViewModel : ObservableObject
{
    private readonly VariablesViewModel _owner;
    private bool _updating;

    internal VariableRowViewModel(VariablesViewModel owner, int index)
    {
        _owner = owner;
        Index = index;
    }

    /// <summary>Position in the document.</summary>
    public int Index { get; }

    /// <summary>Name.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>Data type.</summary>
    [ObservableProperty]
    public partial string Type { get; set; } = string.Empty;

    /// <summary>Default as JSON (blank: none).</summary>
    [ObservableProperty]
    public partial string DefaultJson { get; set; } = string.Empty;

    /// <summary>Validation messages.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrors))]
    public partial string ErrorText { get; private set; } = string.Empty;

    /// <summary>Whether validation reported problems.</summary>
    public bool HasErrors => ErrorText.Length > 0;

    /// <summary>Applies the row as one undoable edit.</summary>
    [RelayCommand]
    public void Commit()
    {
        if (!_updating)
        {
            _owner.Commit(this);
        }
    }

    internal void Update(VariableDraft variable, string errors)
    {
        _updating = true;
        try
        {
            Name = variable.Name;
            Type = variable.Type;
            DefaultJson = variable.DefaultJson ?? string.Empty;
            ErrorText = errors;
        }
        finally
        {
            _updating = false;
        }
    }

    // A type picked from the list is committed immediately; text fields commit on Enter/leaving.
    partial void OnTypeChanged(string value) => Commit();
}

/// <summary>The Arguments pane: a grid of the workflow's arguments (name, direction, type, required, default).</summary>
/// <param name="editor">Applies edits.</param>
public sealed partial class ArgumentsViewModel(IDocumentEditor editor) : ObservableObject
{
    private readonly IDocumentEditor _editor = editor ?? throw new ArgumentNullException(nameof(editor));

    /// <summary>Rows.</summary>
    public ObservableCollection<ArgumentRowViewModel> Rows { get; } = [];

    /// <summary>Shows the document's arguments.</summary>
    /// <param name="draft">The document.</param>
    /// <param name="diagnostics">All diagnostics.</param>
    public void Load(WorkflowDraft draft, IReadOnlyList<DraftDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (Rows.Count != draft.Arguments.Count)
        {
            Rows.Clear();
            for (var i = 0; i < draft.Arguments.Count; i++)
            {
                Rows.Add(new ArgumentRowViewModel(this, i));
            }
        }

        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i].Update(draft.Arguments[i], DataChoices.Messages(diagnostics.Where(d => d.ArgumentIndex == i)));
        }
    }

    /// <summary>The edit that would apply rows typed into but not committed yet; null when nothing is pending.</summary>
    internal Func<WorkflowDraft, WorkflowDraft>? PendingEdit()
    {
        var current = _editor.Document.Arguments;
        var pending = Rows.Where(r => r.Index < current.Count).Select(r => (r.Index, Next: Draft(r))).Where(p => p.Next != current[p.Index]).ToList();
        return pending.Count == 0 ? null : d => pending.Aggregate(d, (acc, p) => DraftEdits.SetArgument(acc, p.Index, p.Next));
    }

    internal void Commit(ArgumentRowViewModel row)
    {
        var next = Draft(row);
        if (next != _editor.Document.Arguments[row.Index] && !_editor.Edit(d => DraftEdits.SetArgument(d, row.Index, next), $"Edit argument {next.Name}"))
        {
            row.Update(_editor.Document.Arguments[row.Index], row.ErrorText);
        }
    }

    private static ArgumentDraft Draft(ArgumentRowViewModel row) =>
        new(row.Name.Trim(), row.Direction, row.Type, row.Required, DataChoices.DefaultOrNull(row.DefaultJson));

    [RelayCommand]
    private void Add()
    {
        var name = DataChoices.UniqueName("argument", _editor.Document.Arguments.Select(a => a.Name).Concat(_editor.Document.Variables.Select(v => v.Name)));
        _editor.Edit(d => DraftEdits.SetArgument(d, null, new ArgumentDraft(name, "In", nameof(WorkflowDataType.String))), $"Add argument {name}");
    }

    [RelayCommand]
    private void Remove(ArgumentRowViewModel? row)
    {
        if (row is not null)
        {
            _editor.Edit(d => DraftEdits.RemoveArgument(d, row.Index), $"Remove argument {row.Name}");
        }
    }
}

/// <summary>One argument.</summary>
public sealed partial class ArgumentRowViewModel : ObservableObject
{
    private readonly ArgumentsViewModel _owner;
    private bool _updating;

    internal ArgumentRowViewModel(ArgumentsViewModel owner, int index)
    {
        _owner = owner;
        Index = index;
    }

    /// <summary>Position in the document.</summary>
    public int Index { get; }

    /// <summary>Name.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>In, Out or InOut.</summary>
    [ObservableProperty]
    public partial string Direction { get; set; } = string.Empty;

    /// <summary>Data type.</summary>
    [ObservableProperty]
    public partial string Type { get; set; } = string.Empty;

    /// <summary>Whether a value must be supplied.</summary>
    [ObservableProperty]
    public partial bool Required { get; set; }

    /// <summary>Default as JSON (blank: none).</summary>
    [ObservableProperty]
    public partial string DefaultJson { get; set; } = string.Empty;

    /// <summary>Validation messages.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrors))]
    public partial string ErrorText { get; private set; } = string.Empty;

    /// <summary>Whether validation reported problems.</summary>
    public bool HasErrors => ErrorText.Length > 0;

    /// <summary>Applies the row as one undoable edit.</summary>
    [RelayCommand]
    public void Commit()
    {
        if (!_updating)
        {
            _owner.Commit(this);
        }
    }

    internal void Update(ArgumentDraft argument, string errors)
    {
        _updating = true;
        try
        {
            Name = argument.Name;
            Direction = argument.Direction;
            Type = argument.Type;
            Required = argument.Required;
            DefaultJson = argument.DefaultJson ?? string.Empty;
            ErrorText = errors;
        }
        finally
        {
            _updating = false;
        }
    }

    partial void OnDirectionChanged(string value) => Commit();

    partial void OnTypeChanged(string value) => Commit();

    partial void OnRequiredChanged(bool value) => Commit();
}
