using MyRPA.Core.Activities;
using MyRPA.Studio.Documents;
using MyRPA.Studio.Editing;
using MyRPA.Workflow.Validation;

namespace MyRPA.Studio.Tests;

public sealed class NodePathTests
{
    [Fact]
    public void Paths_HaveValueEquality_AndAncestry()
    {
        var a = NodePath.Root.Child(1).Slot("then");
        var b = NodePath.Root.Child(1).Slot("then");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(NodePath.Root.Child(1).IsAncestorOrSelfOf(a));
        Assert.False(NodePath.Root.Child(0).IsAncestorOrSelfOf(a));
        Assert.Equal(NodePath.Root.Child(1), a.Parent);
        Assert.Equal("/children[1]/slots[then]", a.ToString());
    }

    [Fact]
    public void Find_And_Replace_AddressNestedNodes()
    {
        var draft = Drafts.Sample();
        var path = NodePath.Root.Child(1).Slot("then");

        Assert.Equal("say", DraftTree.Find(draft, path)!.Id);
        Assert.Null(DraftTree.Find(draft, NodePath.Root.Child(9)));
        var replaced = DraftTree.Replace(draft, path, new NodeDraft("new", "Core.Log"));
        Assert.Equal("new", DraftTree.Get(replaced, path).Id);
        Assert.Equal("say", DraftTree.Get(draft, path).Id); // immutable
        Assert.Equal(4, DraftTree.Walk(draft).Count());
    }
}

public sealed class EditingTests
{
    private readonly DraftEdits _edits = new(Catalogs.BuiltIn);

    private static ActivityDescriptor Descriptor(string type) => Catalogs.BuiltIn.Descriptors.Single(d => d.TypeName.Value == type);

    [Fact]
    public void Insert_AddsChildrenAndFillsSlots()
    {
        var draft = Drafts.Sample();
        var log = DraftEdits.CreateNode(draft, Descriptor("Core.Log"));

        var (withChild, childPath) = _edits.Insert(draft, NodePath.Root, new ChildPosition(0), log);
        var (withElse, elsePath) = _edits.Insert(withChild, NodePath.Root.Child(2), new SlotPosition("else"), DraftEdits.CreateNode(withChild, Descriptor("Core.Throw")));

        Assert.Equal("log-1", DraftTree.Get(withChild, childPath).Id);
        Assert.Equal(3, withChild.Root.Children.Count);
        Assert.Equal("Core.Throw", DraftTree.Get(withElse, elsePath).Type);
        Assert.Equal(NodePath.Root.Child(2).Slot("else"), elsePath);
    }

    [Fact]
    public void CreateNode_GivesUniqueReadableIds()
    {
        var draft = WorkflowDraft.CreateNew();
        var first = DraftEdits.CreateNode(draft, Descriptor("Core.Log"));
        draft = _edits.Insert(draft, NodePath.Root, new ChildPosition(0), first).Draft;

        var second = DraftEdits.CreateNode(draft, Descriptor("Core.Log"));

        Assert.Equal("log-1", first.Id);
        Assert.Equal("log-2", second.Id);
    }

    [Theory]
    [InlineData("/children[0]", "children", "does not contain a list")] // Assign has no children
    [InlineData("/children[1]", "slot:then", "already contains")]
    [InlineData("/children[1]", "slot:body", "has no slot")]
    [InlineData("/", "index:7", "outside the list")]
    public void Insert_RefusesInvalidPlaces(string parent, string where, string reason)
    {
        var draft = Drafts.Sample();
        var parentPath = parent == "/" ? NodePath.Root : NodePath.Root.Child(int.Parse(parent["/children[".Length..^1], System.Globalization.CultureInfo.InvariantCulture));
        InsertPosition position = where switch
        {
            "children" => new ChildPosition(0),
            _ when where.StartsWith("slot:", StringComparison.Ordinal) => new SlotPosition(where[5..]),
            _ => new ChildPosition(int.Parse(where[6..], System.Globalization.CultureInfo.InvariantCulture)),
        };

        var error = Assert.Throws<EditException>(() => _edits.Insert(draft, parentPath, position, new NodeDraft("x", "Core.Log")));

        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Insert_IntoAnUnknownActivity_IsRefused()
    {
        var draft = WorkflowDraft.CreateNew() with { Root = new NodeDraft("r", "Plugin.Missing") };

        Assert.False(_edits.CanInsert(draft, NodePath.Root, new ChildPosition(0), out var reason));
        Assert.Contains("not a registered activity", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_DeletesTheSubtree_ButNeverTheRoot()
    {
        var draft = Drafts.Sample();

        var removed = DraftEdits.Remove(draft, NodePath.Root.Child(1));

        Assert.Single(removed.Root.Children);
        Assert.DoesNotContain(removed.Root.DescendantsAndSelf(), n => n.Id == "say");
        Assert.Throws<EditException>(() => DraftEdits.Remove(draft, NodePath.Root));
        var slotRemoved = DraftEdits.Remove(draft, NodePath.Root.Child(1).Slot("then"));
        Assert.Empty(DraftTree.Get(slotRemoved, NodePath.Root.Child(1)).Slots);
    }

    [Theory]
    [InlineData(0, 2, new[] { "check", "set" })] // move first to the end
    [InlineData(1, 0, new[] { "check", "set" })] // move second to the front
    [InlineData(0, 0, new[] { "set", "check" })] // no-op
    [InlineData(0, 1, new[] { "set", "check" })] // no-op (same place)
    public void Move_WithinTheSameList(int from, int to, string[] expected)
    {
        var (moved, _) = _edits.Move(Drafts.Sample(), NodePath.Root.Child(from), NodePath.Root, new ChildPosition(to));

        Assert.Equal(expected, moved.Root.Children.Select(c => c.Id));
    }

    [Fact]
    public void Move_IntoASlot_AndBackIntoTheList()
    {
        var draft = Drafts.Sample();

        var (intoElse, elsePath) = _edits.Move(draft, NodePath.Root.Child(0), NodePath.Root.Child(1), new SlotPosition("else"));
        var (back, backPath) = _edits.Move(intoElse, elsePath, NodePath.Root, new ChildPosition(1));

        Assert.Equal(NodePath.Root.Child(0).Slot("else"), elsePath); // `check` shifted to index 0 after `set` left the list
        Assert.Equal("set", DraftTree.Get(intoElse, elsePath).Id);
        Assert.Equal(["check", "set"], back.Root.Children.Select(c => c.Id));
        Assert.Equal(NodePath.Root.Child(1), backPath);
    }

    [Fact]
    public void Move_IntoItselfOrOfTheRoot_IsRefused()
    {
        var draft = Drafts.Sample();

        Assert.Throws<EditException>(() => _edits.Move(draft, NodePath.Root.Child(1), NodePath.Root.Child(1), new SlotPosition("else")));
        Assert.Throws<EditException>(() => _edits.Move(draft, NodePath.Root, NodePath.Root.Child(1), new SlotPosition("else")));
    }

    [Fact]
    public void SetProperty_AddsReplacesAndRemoves()
    {
        var draft = Drafts.Sample();
        var path = NodePath.Root.Child(1).Slot("then");

        var added = DraftEdits.SetProperty(draft, path, "level", new ScalarValue("Warning"));
        var replaced = DraftEdits.SetProperty(added, path, "message", new ScalarValue("'hi'"));
        var removed = DraftEdits.SetProperty(replaced, path, "level", null);

        Assert.Equal(new ScalarValue("Warning"), DraftTree.Get(added, path).Property("level"));
        Assert.Equal(new ScalarValue("'hi'"), DraftTree.Get(replaced, path).Property("message"));
        Assert.Null(DraftTree.Get(removed, path).Property("level"));
        Assert.Same(draft, DraftEdits.SetProperty(draft, path, "absent", null));
    }

    [Fact]
    public void ArgumentsAndVariables_CheckDefaultsAreJson()
    {
        var draft = Drafts.Sample();

        var added = DraftEdits.SetVariable(draft, null, new VariableDraft("items", "List", "[1, 2]"));
        var changed = DraftEdits.SetArgument(added, 0, new ArgumentDraft("who", "In", "String", Required: true));
        var removed = DraftEdits.RemoveVariable(changed, 0);

        Assert.Equal(2, added.Variables.Count);
        Assert.True(changed.Arguments[0].Required);
        Assert.Equal("items", Assert.Single(removed.Variables).Name);
        Assert.Throws<EditException>(() => DraftEdits.SetVariable(draft, null, new VariableDraft("v", "String", "not json")));
        Assert.Throws<EditException>(() => DraftEdits.RemoveArgument(draft, 5));
    }
}

public sealed class ClipboardAndHistoryTests
{
    [Fact]
    public void Clipboard_RoundTripsNodesWithTheirSubtrees()
    {
        var check = Drafts.Sample().Root.Children[1];

        Assert.True(DraftClipboard.TryDeserialize(DraftClipboard.Serialize([check]), out var nodes));

        Assert.Equal(DraftJson.WriteNode(check), DraftJson.WriteNode(Assert.Single(nodes)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("{ \"id\": \"x\" }")]
    [InlineData("{ \"myrpaNodes\": \"1.0\", \"nodes\": [] }")]
    public void Clipboard_IgnoresOtherText(string? text) => Assert.False(DraftClipboard.TryDeserialize(text, out _));

    [Fact]
    public void Paste_RenamesCollidingIds_IncludingDescendantsAndRepeats()
    {
        var draft = Drafts.Sample();
        var check = draft.Root.Children[1];

        var pasted = DraftClipboard.PrepareForPaste([check, check, new NodeDraft("fresh", "Core.Log")], draft);

        Assert.Equal("if-1", pasted[0].Id);
        Assert.Equal("log-1", pasted[0].Slot("then")!.Id);
        Assert.Equal("if-2", pasted[1].Id);
        Assert.Equal("log-2", pasted[1].Slot("then")!.Id);
        Assert.Equal("fresh", pasted[2].Id);
    }

    [Fact]
    public void History_UndoRedo_AndDirtyTracking()
    {
        var start = Drafts.Sample();
        var history = new DocumentHistory(start);
        var changes = 0;
        history.Changed += (_, _) => changes++;

        var a = start with { Name = "A" };
        var b = a with { Name = "B" };
        history.Apply(a, "rename A");
        history.Apply(b, "rename B");
        Assert.True(history.IsDirty);
        Assert.Equal("rename B", history.UndoDescription);

        history.Undo();
        Assert.Same(a, history.Current);
        history.Undo();
        Assert.Same(start, history.Current);
        Assert.False(history.IsDirty);
        Assert.False(history.CanUndo);

        history.Redo();
        Assert.Same(a, history.Current);
        history.Apply(a with { Name = "C" }, "rename C");
        Assert.False(history.CanRedo); // a new edit clears redo
        history.MarkSaved();
        Assert.False(history.IsDirty);
        Assert.False(history.Apply(history.Current, "no-op"));
        Assert.Equal(7, changes); // 3 applies, 2 undos, 1 redo, 1 save
    }

    [Fact]
    public void History_KeepsAtMostMaxUndoSteps()
    {
        var history = new DocumentHistory(WorkflowDraft.CreateNew());
        for (var i = 0; i < DocumentHistory.MaxUndo + 25; i++)
        {
            history.Apply(history.Current with { Name = $"n{i}" }, "edit");
        }

        var undone = 0;
        while (history.CanUndo)
        {
            history.Undo();
            undone++;
        }

        Assert.Equal(DocumentHistory.MaxUndo, undone);
    }
}

public sealed class ValidationMappingTests
{
    private readonly DraftValidator _validator = new(Catalogs.BuiltIn);

    [Fact]
    public void ValidDraft_ProducesARunnableDefinition()
    {
        var result = _validator.Validate(Drafts.Sample());

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(d => d.Diagnostic.ToString())));
        Assert.Equal("sample", result.Workflow!.Id.Value);
    }

    [Fact]
    public void Diagnostics_AreMappedToNodesPropertiesArgumentsAndVariables()
    {
        var draft = Drafts.Sample();
        draft = DraftEdits.SetProperty(draft, NodePath.Root.Child(0), "value", new ScalarValue("1 +"));
        draft = DraftEdits.SetProperty(draft, NodePath.Root.Child(1).Slot("then"), "message", null);
        draft = DraftEdits.SetArgument(draft, 1, new ArgumentDraft("result", "Out", "Text"));
        draft = DraftEdits.SetVariable(draft, 0, new VariableDraft("count", "Int", "\"zero\""));
        draft = DraftTree.Replace(draft, NodePath.Root.Child(1).Slot("then"), DraftTree.Get(draft, NodePath.Root.Child(1).Slot("then")) with { Type = "Plugin.Missing" });

        var diagnostics = _validator.Validate(draft).Diagnostics;
        var seen = string.Join(Environment.NewLine, diagnostics.Select(d => $"{d.Diagnostic} -> node {d.Node} prop {d.Property} arg {d.ArgumentIndex} var {d.VariableIndex}"));

        Assert.True(diagnostics.Any(d => d.Node == NodePath.Root.Child(0) && d.Property == "value"), seen);
        Assert.True(diagnostics.Any(d => d.Node == NodePath.Root.Child(1).Slot("then") && d.Diagnostic.Code == DiagnosticCodes.UnknownActivityType), seen);
        Assert.True(diagnostics.Any(d => d.ArgumentIndex == 1), seen);
        Assert.True(diagnostics.Any(d => d.VariableIndex == 0), seen);
        Assert.All(diagnostics, d => Assert.True(d.Node is not null || d.ArgumentIndex is not null || d.VariableIndex is not null, d.Diagnostic.ToString()));
    }

    [Fact]
    public void SlotNamesWithDots_MapExactly()
    {
        var draft = DraftJson.Read("""
            { "schemaVersion": "1.0", "id": "w", "name": "W", "version": "1",
              "root": { "id": "s", "type": "Core.Switch", "properties": { "expression": "1.5" }, "slots": {
                "case:1.5": { "id": "a", "type": "Core.Log", "properties": { "message": "1 +" } },
                "case:1.5.properties": { "id": "b", "type": "Core.Log", "properties": { "message": "'ok'" } } } } }
            """);

        var diagnostic = Assert.Single(_validator.Validate(draft).Diagnostics);

        Assert.Equal(NodePath.Root.Slot("case:1.5"), diagnostic.Node);
        Assert.Equal("message", diagnostic.Property);
    }
}
