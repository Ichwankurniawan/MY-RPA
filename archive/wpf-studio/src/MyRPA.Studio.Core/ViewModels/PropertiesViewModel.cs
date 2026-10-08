using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRPA.Core.Activities;
using MyRPA.Studio.Documents;
using MyRPA.Studio.Editing;
using MyRPA.Workflow.Expressions;

namespace MyRPA.Studio.ViewModels;

/// <summary>Applies an edit to the open document (implemented by <see cref="StudioViewModel"/>).</summary>
public interface IDocumentEditor
{
    /// <summary>The current document.</summary>
    WorkflowDraft Document { get; }

    /// <summary>Applies an edit as one undoable step; reports refused edits to the user.</summary>
    /// <param name="edit">The edit.</param>
    /// <param name="description">Description for the Edit menu.</param>
    /// <returns>Whether the document changed.</returns>
    bool Edit(Func<WorkflowDraft, WorkflowDraft> edit, string description);
}

/// <summary>
/// The Properties pane: the workflow's identity when nothing is selected, otherwise the selected node's id, display
/// name and one editor per property, generated from the property kinds of the activity's descriptor.
/// </summary>
/// <param name="editor">Applies edits.</param>
public sealed partial class PropertiesViewModel(IDocumentEditor editor) : ObservableObject
{
    private readonly IDocumentEditor _editor = editor ?? throw new ArgumentNullException(nameof(editor));

    /// <summary>The selected node (null: the workflow is shown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorkflow))]
    [NotifyPropertyChangedFor(nameof(IsNode))]
    public partial NodePath? Node { get; private set; }

    /// <summary>Whether the workflow's properties are shown.</summary>
    public bool IsWorkflow => Node is null;

    /// <summary>Whether a node's properties are shown.</summary>
    public bool IsNode => Node is not null;

    /// <summary>Workflow id.</summary>
    [ObservableProperty]
    public partial string WorkflowId { get; set; } = string.Empty;

    /// <summary>Workflow name.</summary>
    [ObservableProperty]
    public partial string WorkflowName { get; set; } = string.Empty;

    /// <summary>Workflow version.</summary>
    [ObservableProperty]
    public partial string WorkflowVersion { get; set; } = string.Empty;

    /// <summary>Workflow description.</summary>
    [ObservableProperty]
    public partial string WorkflowDescription { get; set; } = string.Empty;

    /// <summary>Workflow-level validation messages.</summary>
    [ObservableProperty]
    public partial string WorkflowErrorText { get; set; } = string.Empty;

    /// <summary>Selected node id.</summary>
    [ObservableProperty]
    public partial string NodeId { get; set; } = string.Empty;

    /// <summary>Selected node display name.</summary>
    [ObservableProperty]
    public partial string NodeDisplayName { get; set; } = string.Empty;

    /// <summary>Activity type of the selected node.</summary>
    [ObservableProperty]
    public partial string TypeName { get; private set; } = string.Empty;

    /// <summary>Activity display name and description.</summary>
    [ObservableProperty]
    public partial string TypeDescription { get; private set; } = string.Empty;

    /// <summary>Node-level validation messages (not about a specific property).</summary>
    [ObservableProperty]
    public partial string NodeErrorText { get; set; } = string.Empty;

    /// <summary>Property editors of the selected node.</summary>
    public ObservableCollection<PropertyEditorViewModel> Editors { get; } = [];

    /// <summary>Shows the workflow's identity.</summary>
    /// <param name="draft">The document.</param>
    /// <param name="diagnostics">Workflow-level diagnostics.</param>
    public void ShowWorkflow(WorkflowDraft draft, IEnumerable<DraftDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Node = null;
        Editors.Clear();
        WorkflowId = draft.Id;
        WorkflowName = draft.Name;
        WorkflowVersion = draft.Version;
        WorkflowDescription = draft.Description ?? string.Empty;
        WorkflowErrorText = Messages(diagnostics);
    }

    /// <summary>Shows a node. Editors are updated in place when the same node is shown again.</summary>
    /// <param name="draft">The document.</param>
    /// <param name="path">The node.</param>
    /// <param name="descriptor">Its activity metadata (null for unregistered types).</param>
    /// <param name="diagnostics">Diagnostics of this node.</param>
    public void ShowNode(WorkflowDraft draft, NodePath path, ActivityDescriptor? descriptor, IReadOnlyList<DraftDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var node = DraftTree.Get(draft, path);
        var targets = WritableNames(draft);
        var names = (descriptor?.Properties.Select(p => p.Name) ?? [])
            .Concat(node.Properties.Select(p => p.Name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var sameLayout = path.Equals(Node) && Editors.Select(e => e.Name).SequenceEqual(names, StringComparer.Ordinal);
        Node = path;
        NodeId = node.Id;
        NodeDisplayName = node.DisplayName ?? string.Empty;
        TypeName = node.Type;
        TypeDescription = descriptor is null
            ? $"'{node.Type}' is not a registered activity (is its plugin loaded?)."
            : $"{descriptor.DisplayName} — {descriptor.Description}".TrimEnd(' ', '—');
        NodeErrorText = Messages(diagnostics.Where(d => d.Property is null));

        if (!sameLayout)
        {
            Editors.Clear();
            foreach (var name in names)
            {
                var definition = descriptor?.FindProperty(name);
                var editorPath = path;
                Editors.Add(new PropertyEditorViewModel(
                    name,
                    definition,
                    definition?.Kind is ActivityPropertyKind.AssignmentTarget or ActivityPropertyKind.AssignmentTargetMap ? targets : [],
                    value => _editor.Edit(d => DraftEdits.SetProperty(d, editorPath, name, value), $"Edit {name}")));
            }
        }

        foreach (var editorViewModel in Editors)
        {
            editorViewModel.Update(node.Property(editorViewModel.Name), Messages(diagnostics.Where(d => d.Property == editorViewModel.Name)));
        }
    }

    /// <summary>Clears the pane (no document node or workflow shown).</summary>
    public void Clear()
    {
        Node = null;
        Editors.Clear();
    }

    /// <summary>
    /// The edit that would apply values typed into this pane but not committed yet (Enter or leaving the field commits
    /// them); null when nothing is pending. Values are captured now, so the edit can be combined with others.
    /// </summary>
    internal Func<WorkflowDraft, WorkflowDraft>? PendingEdit()
    {
        var draft = _editor.Document;
        if (Node is not { } path)
        {
            var (id, name, version, description) = (WorkflowId, WorkflowName, WorkflowVersion, WorkflowDescription);
            var unchanged = id.Trim() == draft.Id && name == draft.Name && version.Trim() == draft.Version
                && (string.IsNullOrWhiteSpace(description) ? null : description) == draft.Description;
            return unchanged ? null : d => DraftEdits.SetInfo(d, id, name, version, description);
        }

        if (DraftTree.Find(draft, path) is not { } node)
        {
            return null;
        }

        var (nodeId, displayName) = (NodeId, NodeDisplayName);
        var headerChanged = nodeId.Trim() != node.Id || (string.IsNullOrWhiteSpace(displayName) ? null : displayName) != node.DisplayName;
        var properties = new List<(string Name, PropertyDraft? Value)>();
        foreach (var editor in Editors)
        {
            if (editor.TryGetPendingValue(out var value))
            {
                properties.Add((editor.Name, value));
            }
        }

        if (!headerChanged && properties.Count == 0)
        {
            return null;
        }

        return d =>
        {
            if (headerChanged)
            {
                d = DraftEdits.SetDisplayName(DraftEdits.SetId(d, path, nodeId), path, displayName);
            }

            return properties.Aggregate(d, (current, p) => DraftEdits.SetProperty(current, path, p.Name, p.Value));
        };
    }

    [RelayCommand]
    private void CommitWorkflowInfo() =>
        _editor.Edit(d => DraftEdits.SetInfo(d, WorkflowId, WorkflowName, WorkflowVersion, WorkflowDescription), "Edit workflow properties");

    [RelayCommand]
    private void CommitNodeHeader()
    {
        if (Node is not { } path)
        {
            return;
        }

        _editor.Edit(d => DraftEdits.SetDisplayName(DraftEdits.SetId(d, path, NodeId), path, NodeDisplayName), "Edit activity id/name");
    }

    /// <summary>Variables and Out/InOut arguments: the names an assignment target can use.</summary>
    /// <param name="draft">The document.</param>
    internal static IReadOnlyList<string> WritableNames(WorkflowDraft draft) =>
        [.. draft.Variables.Select(v => v.Name)
            .Concat(draft.Arguments.Where(a => a.Direction is "Out" or "InOut").Select(a => a.Name))
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    private static string Messages(IEnumerable<DraftDiagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(d => $"{(d.IsError ? "✖" : "⚠")} {d.Diagnostic.Message}"));
}

/// <summary>
/// An editor for one property. Its shape follows the property kind: expression (with live syntax feedback), text or a
/// choice of allowed values, assignment target (with suggested variable names), local name, or a key/value map.
/// Values are committed as one undoable edit (on Enter or when leaving the field); a blank value removes the property.
/// </summary>
public sealed partial class PropertyEditorViewModel : ObservableObject
{
    private readonly Action<PropertyDraft?> _commit;
    private PropertyDraft? _value;
    private bool _updating;

    /// <summary>Creates the editor.</summary>
    /// <param name="name">Property name.</param>
    /// <param name="definition">Its definition (null for a property the activity does not declare).</param>
    /// <param name="suggestions">Suggested names for assignment targets.</param>
    /// <param name="commit">Applies a new value.</param>
    public PropertyEditorViewModel(string name, ActivityPropertyDefinition? definition, IReadOnlyList<string> suggestions, Action<PropertyDraft?> commit)
    {
        Name = name;
        Definition = definition;
        Suggestions = suggestions;
        _commit = commit ?? throw new ArgumentNullException(nameof(commit));
    }

    /// <summary>Property name.</summary>
    public string Name { get; }

    /// <summary>The property definition, or null when the activity does not declare it.</summary>
    public ActivityPropertyDefinition? Definition { get; }

    /// <summary>The kind (null for undeclared properties).</summary>
    public ActivityPropertyKind? Kind => Definition?.Kind;

    /// <summary>Label with requirement marker, e.g. <c>message *</c>.</summary>
    public string Label => Definition?.IsRequired == true ? Name + " *" : Name;

    /// <summary>Kind shown under the label.</summary>
    public string KindLabel => Kind switch
    {
        ActivityPropertyKind.Expression => "expression",
        ActivityPropertyKind.Text => "text",
        ActivityPropertyKind.AssignmentTarget => "variable to assign",
        ActivityPropertyKind.LocalName => "local name",
        ActivityPropertyKind.ExpressionMap => "name → expression",
        ActivityPropertyKind.AssignmentTargetMap => "name → variable",
        _ => "not declared by this activity",
    };

    /// <summary>Description (tooltip).</summary>
    public string Description => Definition?.Description ?? string.Empty;

    /// <summary>Allowed values (a choice when not empty).</summary>
    public IReadOnlyList<string> AllowedValues => Definition?.AllowedValues ?? [];

    /// <summary>Whether the editor is a choice of allowed values.</summary>
    public bool IsChoice => AllowedValues.Count > 0;

    /// <summary>Suggested variable names (assignment targets).</summary>
    public IReadOnlyList<string> Suggestions { get; }

    /// <summary>Whether suggestions are offered (an editable choice).</summary>
    public bool HasSuggestions => Suggestions.Count > 0 && !IsMap;

    /// <summary>Values offered in the editor's list: the allowed values, otherwise the suggested names.</summary>
    public IReadOnlyList<string> Options => IsChoice ? AllowedValues : Suggestions;

    /// <summary>Whether the editor offers a list (an editable combo box; blank removes the value).</summary>
    public bool HasOptions => !IsMap && Options.Count > 0;

    /// <summary>Whether the editor is a key/value map.</summary>
    public bool IsMap => Kind is ActivityPropertyKind.ExpressionMap or ActivityPropertyKind.AssignmentTargetMap || _value is MapValue;

    /// <summary>Whether the editor is a single text box.</summary>
    public bool IsText => !IsMap && !HasOptions;

    /// <summary>The value text (single-value editors).</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>Map entries (map editors).</summary>
    public ObservableCollection<MapEntryViewModel> Entries { get; } = [];

    /// <summary>Validation messages for this property.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrors))]
    public partial string ErrorText { get; private set; } = string.Empty;

    /// <summary>Whether validation reported problems.</summary>
    public bool HasErrors => ErrorText.Length > 0;

    /// <summary>Live feedback while typing an expression (syntax only).</summary>
    [ObservableProperty]
    public partial string HintText { get; private set; } = string.Empty;

    /// <summary>Shows a value from the document (after load, commit, undo …).</summary>
    /// <param name="value">The property value.</param>
    /// <param name="errors">Validation messages.</param>
    public void Update(PropertyDraft? value, string errors)
    {
        _updating = true;
        try
        {
            _value = value;
            ErrorText = errors ?? string.Empty;
            if (value is MapValue map)
            {
                if (!Entries.Select(e => (e.Key, e.Value)).SequenceEqual(map.Entries.Select(e => (e.Key, e.Value.Text))))
                {
                    Entries.Clear();
                    foreach (var entry in map.Entries)
                    {
                        Entries.Add(new MapEntryViewModel(this, entry.Key, entry.Value.Text));
                    }
                }
            }
            else
            {
                Text = value is ScalarValue scalar ? scalar.Text : string.Empty;
                if (IsMap)
                {
                    Entries.Clear();
                }
            }
        }
        finally
        {
            _updating = false;
        }

        UpdateHint();
    }

    /// <summary>Applies the edited value as one undoable edit (no-op when unchanged).</summary>
    [RelayCommand]
    public void Commit()
    {
        if (_updating)
        {
            return;
        }

        if (TryGetPendingValue(out var next))
        {
            _commit(next);
        }
    }

    /// <summary>Whether the editor holds a value that differs from the document (typed but not committed).</summary>
    /// <param name="value">The value the editor would commit.</param>
    internal bool TryGetPendingValue(out PropertyDraft? value)
    {
        value = IsMap ? BuildMap() : BuildScalar();
        return !_updating && !SameValue(value, _value);
    }

    [RelayCommand]
    private void AddEntry() => Entries.Add(new MapEntryViewModel(this, string.Empty, string.Empty));

    [RelayCommand]
    private void RemoveEntry(MapEntryViewModel? entry)
    {
        if (entry is not null && Entries.Remove(entry))
        {
            Commit();
        }
    }

    partial void OnTextChanged(string value) => UpdateHint();

    private void UpdateHint()
    {
        if (Kind != ActivityPropertyKind.Expression || string.IsNullOrWhiteSpace(Text) || (_value is ScalarValue { IsJsonLiteral: true } literal && literal.Text == Text))
        {
            HintText = string.Empty;
            return;
        }

        HintText = WorkflowExpression.TryParse(Text, out _, out var error) ? string.Empty : error.Message;
    }

    private ScalarValue? BuildScalar()
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            return null;
        }

        // Keep an unchanged JSON literal (e.g. 500) as it was written.
        return _value is ScalarValue original && original.Text == Text ? original : new ScalarValue(Text);
    }

    private MapValue? BuildMap()
    {
        var originals = (_value as MapValue)?.Entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal) ?? [];
        var entries = Entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Key))
            .Select(e => new MapEntry(e.Key.Trim(), originals.TryGetValue(e.Key.Trim(), out var o) && o.Text == e.Value ? o : new ScalarValue(e.Value)))
            .ToList();
        return entries.Count == 0 ? null : new MapValue([.. entries]);
    }

    private static bool SameValue(PropertyDraft? a, PropertyDraft? b) => (a, b) switch
    {
        (null, null) => true,
        (ScalarValue x, ScalarValue y) => x == y,
        (MapValue x, MapValue y) => x.Entries.SequenceEqual(y.Entries),
        _ => false,
    };
}

/// <summary>One key/value row of a map property.</summary>
public sealed partial class MapEntryViewModel : ObservableObject
{
    private readonly PropertyEditorViewModel _owner;

    /// <summary>Creates the row.</summary>
    /// <param name="owner">The map editor.</param>
    /// <param name="key">Key.</param>
    /// <param name="value">Value text.</param>
    public MapEntryViewModel(PropertyEditorViewModel owner, string key, string value)
    {
        _owner = owner;
        Key = key;
        Value = value;
    }

    /// <summary>Key.</summary>
    [ObservableProperty]
    public partial string Key { get; set; }

    /// <summary>Value text.</summary>
    [ObservableProperty]
    public partial string Value { get; set; }

    /// <summary>Commits the whole map.</summary>
    [RelayCommand]
    private void Commit() => _owner.Commit();
}
