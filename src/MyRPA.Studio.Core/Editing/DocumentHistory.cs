using MyRPA.Studio.Documents;

namespace MyRPA.Studio.Editing;

/// <summary>
/// Undo/redo by snapshots (ADR-0018): every edit stores the previous immutable draft, so undo restores exactly the
/// state before the edit — no inverse operations to get wrong. Dirty tracking compares the current snapshot with the
/// one last saved.
/// </summary>
public sealed class DocumentHistory
{
    /// <summary>Maximum number of undo steps kept.</summary>
    public const int MaxUndo = 200;

    private readonly LinkedList<(WorkflowDraft Draft, string Description)> _undo = new();
    private readonly Stack<(WorkflowDraft Draft, string Description)> _redo = new();
    private WorkflowDraft _saved;

    /// <summary>Starts a history at <paramref name="initial"/> (considered saved).</summary>
    /// <param name="initial">The opened or new document.</param>
    public DocumentHistory(WorkflowDraft initial)
    {
        Current = initial ?? throw new ArgumentNullException(nameof(initial));
        _saved = initial;
    }

    /// <summary>Raised after every change of <see cref="Current"/> (edit, undo, redo, reset).</summary>
    public event EventHandler? Changed;

    /// <summary>The current document.</summary>
    public WorkflowDraft Current { get; private set; }

    /// <summary>Whether the document changed since it was opened or saved.</summary>
    public bool IsDirty => !ReferenceEquals(Current, _saved);

    /// <summary>Whether there is something to undo.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Whether there is something to redo.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Description of the edit <see cref="Undo"/> would revert.</summary>
    public string? UndoDescription => _undo.Last?.Value.Description;

    /// <summary>Description of the edit <see cref="Redo"/> would reapply.</summary>
    public string? RedoDescription => _redo.Count > 0 ? _redo.Peek().Description : null;

    /// <summary>Records an edit. No-op when <paramref name="next"/> is the current document.</summary>
    /// <param name="next">The edited document.</param>
    /// <param name="description">Human-readable description (for the Edit menu).</param>
    /// <returns>Whether the document changed.</returns>
    public bool Apply(WorkflowDraft next, string description)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (ReferenceEquals(next, Current))
        {
            return false;
        }

        _undo.AddLast((Current, description));
        if (_undo.Count > MaxUndo)
        {
            _undo.RemoveFirst();
        }

        _redo.Clear();
        Current = next;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Reverts the last edit.</summary>
    public void Undo()
    {
        if (_undo.Last is not { } last)
        {
            return;
        }

        _undo.RemoveLast();
        _redo.Push((Current, last.Value.Description));
        Current = last.Value.Draft;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reapplies the last undone edit.</summary>
    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        var (draft, description) = _redo.Pop();
        _undo.AddLast((Current, description));
        Current = draft;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Marks the current document as saved.</summary>
    public void MarkSaved()
    {
        _saved = Current;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
