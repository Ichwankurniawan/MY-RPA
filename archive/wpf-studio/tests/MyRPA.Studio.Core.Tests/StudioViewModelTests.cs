using MyRPA.Core.Activities;
using MyRPA.Studio.Documents;
using MyRPA.Studio.Services;
using MyRPA.Studio.ViewModels;

namespace MyRPA.Studio.Tests;

public sealed class StudioEditingTests : IDisposable
{
    private readonly StudioHarness _studio = new();

    private StudioViewModel Vm => _studio.ViewModel;

    public void Dispose() => _studio.Dispose();

    [Fact]
    public void NewStudio_ShowsAnUntitledValidEmptySequence()
    {
        Assert.Equal("Untitled — MyRPA Studio", Vm.Title);
        Assert.False(Vm.IsDirty);
        Assert.Equal("Core.Sequence", Vm.Root.TypeName);
        Assert.True(Vm.Properties.IsWorkflow);
        Assert.Equal("new-workflow", Vm.Properties.WorkflowId);
        Assert.Empty(Vm.Errors);
        Assert.Equal("Valid", Vm.StatusText);
    }

    [Fact]
    public void DropFromToolbox_InsertsSelectsAndValidatesTheNewActivity()
    {
        var zone = EndZone(Vm.Root);

        Assert.True(Vm.CanDrop(Payload("Core.Log"), zone));
        Assert.True(Vm.Drop(Payload("Core.Log"), zone));

        var log = Assert.Single(Vm.Root.Items.OfType<NodeViewModel>());
        Assert.Equal("log-1", log.Id);
        Assert.True(log.IsSelected);
        Assert.Same(log, Vm.SelectedNode);
        Assert.True(log.HasErrors); // "message" is required
        // ADR-0026: the missing required property is now located at the property itself.
        Assert.Contains(Vm.Errors, e => e.Location == "log-1.message" && e.Message.Contains("'message'", StringComparison.Ordinal));
        Assert.NotEmpty(Vm.Properties.Editors.Single(e => e.Name == "message").ErrorText);
        Assert.Equal(["message", "level"], Vm.Properties.Editors.Select(e => e.Name));
        Assert.True(Vm.IsDirty);
        Assert.Equal("Untitled * — MyRPA Studio", Vm.Title);
        Assert.Equal("Undo Add Log", Vm.UndoText);
    }

    [Fact]
    public void PropertyEditor_CommitsOneUndoableEdit_AndClearsTheError()
    {
        Vm.Drop(Payload("Core.Log"), EndZone(Vm.Root));
        var message = Vm.Properties.Editors.Single(e => e.Name == "message");

        message.Text = "'hi' +";
        Assert.NotEmpty(message.HintText); // live syntax feedback before committing
        message.Text = "'hi'";
        Assert.Empty(message.HintText);
        message.Commit();

        Assert.Empty(Vm.Errors);
        Assert.False(Vm.Root.Items.OfType<NodeViewModel>().Single().HasErrors);
        Assert.Equal(new ScalarValue("'hi'"), Vm.Document.Root.Children[0].Property("message"));
        Assert.Same(message, Vm.Properties.Editors.Single(e => e.Name == "message")); // edited in place, focus kept

        Vm.UndoCommand.Execute(null);
        Assert.Null(Vm.Document.Root.Children[0].Property("message"));
        Assert.Equal(string.Empty, message.Text);
        Vm.RedoCommand.Execute(null);
        Assert.Equal("'hi'", message.Text);
    }

    [Fact]
    public void ChoiceAndTargetEditors_OfferAllowedValuesAndWritableNames()
    {
        Vm.Edit(_ => Drafts.Sample(), "load");
        Vm.SelectPath(NodePath.Root.Child(0)); // Assign
        var to = Vm.Properties.Editors.Single(e => e.Name == "to");
        Assert.Equal(["count", "result"], to.Suggestions);
        Assert.True(to.HasSuggestions);

        Vm.SelectPath(NodePath.Root.Child(1).Slot("then")); // Log
        var level = Vm.Properties.Editors.Single(e => e.Name == "level");
        Assert.True(level.IsChoice);
        Assert.Contains("Warning", level.AllowedValues);
    }

    [Fact]
    public void DropOnCaseZone_AsksForTheCaseValue()
    {
        Vm.Drop(Payload("Core.Switch"), EndZone(Vm.Root));
        var caseZone = Vm.Root.Items.OfType<NodeViewModel>().Single().Slots.Single(s => s.EmptyZone?.NeedsSlotName == true).EmptyZone!;
        _studio.Dialogs.TextAnswer = "gold";

        Assert.True(Vm.Drop(Payload("Core.Log"), caseZone));

        Assert.NotNull(Vm.Document.Root.Children[0].Slot("case:gold"));
        Assert.Equal(NodePath.Root.Child(0).Slot("case:gold"), Vm.SelectedNode!.Path);
    }

    [Fact]
    public void DropOnCaseZone_Cancelled_ChangesNothing()
    {
        Vm.Drop(Payload("Core.Switch"), EndZone(Vm.Root));
        var before = Vm.Document;
        var caseZone = Vm.Root.Items.OfType<NodeViewModel>().Single().Slots.Single(s => s.EmptyZone?.NeedsSlotName == true).EmptyZone!;
        _studio.Dialogs.TextAnswer = null;

        Assert.False(Vm.Drop(Payload("Core.Log"), caseZone));
        Assert.Same(before, Vm.Document);
    }

    [Fact]
    public void Move_IntoItsOwnSubtreeOrOntoItself_IsRefused_ElsewhereMoves()
    {
        Vm.Edit(_ => Drafts.Sample(), "load");
        var check = Vm.Root.Items.OfType<NodeViewModel>().Single(n => n.Id == "check");
        var say = check.Slots.Single(s => s.Name == "then").Node!;
        var elseZone = check.Slots.Single(s => s.Name == "else").EmptyZone!;

        Assert.False(Vm.CanDrop(check.Payload, elseZone)); // into itself
        Assert.False(Vm.CanDrop(check.Payload, Vm.Root.Items.OfType<DropZoneViewModel>().Last())); // same place
        Assert.True(Vm.CanDrop(say.Payload, elseZone));

        Assert.True(Vm.Drop(say.Payload, EndZone(Vm.Root)));

        Assert.Equal(["set", "check", "say"], Vm.Document.Root.Children.Select(c => c.Id));
        Assert.Null(Vm.Document.Root.Children[1].Slot("then"));
    }

    [Fact]
    public void CopyPaste_InsertsACopyWithAFreshId_AfterTheSelection()
    {
        Vm.Edit(_ => Drafts.Sample(), "load");
        Vm.SelectPath(NodePath.Root.Child(0));

        Vm.CopyCommand.Execute(null);
        Vm.PasteCommand.Execute(null);

        Assert.Equal(["set", "assign-1", "check"], Vm.Document.Root.Children.Select(c => c.Id));
        Assert.Equal("assign-1", Vm.SelectedNode!.Id);
    }

    [Fact]
    public void Cut_RemovesTheNode_AndSelectsItsParent()
    {
        Vm.Edit(_ => Drafts.Sample(), "load");
        Vm.SelectPath(NodePath.Root.Child(1).Slot("then"));

        Vm.CutCommand.Execute(null);

        Assert.Null(Vm.Document.Root.Children[1].Slot("then"));
        Assert.Equal("check", Vm.SelectedNode!.Id);
        Assert.Contains("\"say\"", _studio.Clipboard.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RootCannotBeDeleted()
    {
        Vm.SelectPath(NodePath.Root);
        Assert.False(Vm.DeleteCommand.CanExecute(null));
    }

    [Fact]
    public void AddActivity_AppendsToTheSelectedContainer_OrAfterTheSelectedActivity()
    {
        Vm.AddActivityCommand.Execute("Core.Log");
        Vm.AddActivityCommand.Execute("Core.Delay");
        Vm.SelectPath(NodePath.Root.Child(0));
        Vm.AddActivityCommand.Execute("Core.Assign");

        Assert.Equal(["log-1", "assign-1", "delay-1"], Vm.Document.Root.Children.Select(c => c.Id));
    }

    [Fact]
    public void RefusedEdit_IsReportedAndLeavesTheDocumentUnchanged()
    {
        Vm.VariablesCommandAdd();
        var row = Assert.Single(Vm.Variables.Rows);
        var before = Vm.Document;

        row.DefaultJson = "{ not json";
        row.Commit();

        Assert.Same(before, Vm.Document);
        Assert.Single(_studio.Dialogs.Errors);
        Assert.Equal(string.Empty, row.DefaultJson); // reverted to the document's value
    }

    [Fact]
    public void Variables_AndArguments_AreEditedThroughTheGrids()
    {
        Vm.VariablesCommandAdd();
        Vm.Arguments.AddCommand.Execute(null);
        var variable = Vm.Variables.Rows.Single();
        var argument = Vm.Arguments.Rows.Single();

        variable.Name = "total";
        variable.DefaultJson = "5";
        variable.Commit();
        variable.Type = "Int"; // picked from the list: committed immediately
        argument.Direction = "Out";

        Assert.Equal(new VariableDraft("total", "Int", "5"), Vm.Document.Variables.Single());
        Assert.Equal(new ArgumentDraft("argument1", "Out", "String"), Vm.Document.Arguments.Single());
        Assert.Empty(Vm.Errors);

        Vm.Variables.RemoveCommand.Execute(Vm.Variables.Rows.Single());
        Assert.Empty(Vm.Document.Variables);
    }

    [Fact]
    public void Diagnostics_OnArgumentsAreShownOnTheirRows()
    {
        Vm.Arguments.AddCommand.Execute(null);
        var row = Vm.Arguments.Rows.Single();
        row.DefaultJson = "\"text\"";
        row.Type = "Int";

        Assert.True(Vm.Arguments.Rows.Single().HasErrors);
        Assert.Contains(Vm.Errors, e => e.Location == "argument argument1");
    }

    [Fact]
    public void GoToError_SelectsTheNode()
    {
        Vm.AddActivityCommand.Execute("Core.Throw");
        Vm.SelectPath(null);

        Vm.GoToErrorCommand.Execute(Vm.Errors.Single(e => e.Location.StartsWith("throw-1", StringComparison.Ordinal)));

        Assert.Equal("throw-1", Vm.SelectedNode!.Id);
    }

    [Fact]
    public void WorkflowInfo_IsEditedFromThePropertiesPane()
    {
        Vm.Properties.WorkflowName = "Invoice run";
        Vm.Properties.WorkflowId = "bad id";
        Vm.Properties.CommitWorkflowInfoCommand.Execute(null);

        Assert.Equal("Invoice run", Vm.Document.Name);
        Assert.Contains(Vm.Errors, e => e.Location == "workflow");
        Assert.NotEmpty(Vm.Properties.WorkflowErrorText);
    }

    [Fact]
    public async Task SaveThenOpen_RoundTripsTheDocument_AndTracksDirtyState()
    {
        var ct = TestContext.Current.CancellationToken;
        Vm.Edit(_ => Drafts.Sample(), "load");
        _studio.Dialogs.SavePath = "sample.json";

        await Vm.SaveCommand.ExecuteAsync(null);

        Assert.False(Vm.IsDirty);
        Assert.Equal("sample.json — MyRPA Studio", Vm.Title);
        var saved = _studio.Storage.Files[Path.GetFullPath("sample.json")];
        Assert.Equal(DraftJson.Write(Drafts.Sample()), saved);

        await Vm.NewCommand.ExecuteAsync(null);
        Assert.Equal(0, _studio.Dialogs.SaveQuestions); // nothing unsaved
        Assert.True(await Vm.OpenFileAsync("sample.json", ct));
        Assert.Equal(Drafts.Sample(), Vm.Document, DraftComparer.Instance);
        Assert.False(Vm.UndoCommand.CanExecute(null)); // a fresh history
    }

    [Fact]
    public async Task OpenFileAsync_ReportsUnreadableFiles()
    {
        _studio.Storage.Files[Path.GetFullPath("broken.json")] = "{ \"root\": 5 ";

        Assert.False(await Vm.OpenFileAsync("broken.json", TestContext.Current.CancellationToken));
        Assert.False(await Vm.OpenFileAsync("missing.json", TestContext.Current.CancellationToken));

        Assert.Equal(2, _studio.Dialogs.Errors.Count);
        Assert.Null(Vm.FilePath);
    }

    [Theory]
    [InlineData(UnsavedChangesChoice.Cancel, false, false)]
    [InlineData(UnsavedChangesChoice.Discard, true, false)]
    [InlineData(UnsavedChangesChoice.Save, true, true)]
    public async Task ConfirmClose_WithUnsavedChanges_FollowsTheAnswer(UnsavedChangesChoice choice, bool expected, bool expectSaved)
    {
        Vm.AddActivityCommand.Execute("Core.Log");
        _studio.Dialogs.SaveChoice = choice;
        _studio.Dialogs.SavePath = "out.json";

        Assert.Equal(expected, await Vm.ConfirmCloseAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expectSaved, _studio.Storage.Files.ContainsKey(Path.GetFullPath("out.json")));
    }

    [Fact]
    public void Toolbox_ListsCatalogCategories_AndFilters()
    {
        Assert.Equal("Control Flow", Vm.Toolbox.Categories[0].Name);
        Vm.Toolbox.SearchText = "wait";
        var item = Assert.Single(Assert.Single(Vm.Toolbox.Categories).Items);
        Assert.Equal("Core.Delay", item.TypeName);
    }

    [Fact]
    public void UnknownActivity_IsShownWithoutDropZones_AndReported()
    {
        Vm.Edit(d => d with { Root = d.Root with { Children = [new NodeDraft("x", "Acme.Missing")] } }, "load");

        var node = Vm.Root.Items.OfType<NodeViewModel>().Single();
        Assert.False(node.IsKnown);
        Assert.True(node.HasErrors);
        Vm.Select(node);
        Assert.Contains("not a registered activity", Vm.Properties.TypeDescription, StringComparison.Ordinal);
    }

    private static DropZoneViewModel EndZone(NodeViewModel node) => node.Items.OfType<DropZoneViewModel>().Last();

    private static NewActivityPayload Payload(string type) => new(new ActivityTypeName(type));
}

/// <summary>Structural equality for drafts (records with immutable lists compare lists by reference).</summary>
public sealed class DraftComparer : IEqualityComparer<WorkflowDraft>
{
    public static DraftComparer Instance { get; } = new();

    public bool Equals(WorkflowDraft? x, WorkflowDraft? y) =>
        x is not null && y is not null && DraftJson.Write(x) == DraftJson.Write(y);

    public int GetHashCode(WorkflowDraft obj) => DraftJson.Write(obj).GetHashCode(StringComparison.Ordinal);
}

public sealed class StudioRunTests : IDisposable
{
    private readonly StudioHarness _studio = new();

    private StudioViewModel Vm => _studio.ViewModel;

    public void Dispose() => _studio.Dispose();

    [Fact]
    public async Task Run_Succeeds_ShowsOutputsLogsAndCompletedNodes()
    {
        Vm.Edit(_ => Drafts.Sample(), "load");
        _studio.Dialogs.ArgumentAnswers = new Dictionary<string, string> { ["who"] = "Ada" };

        await Vm.RunCommand.ExecuteAsync(null);

        Assert.Equal("Succeeded", Vm.Output.Status);
        Assert.Equal("result = \"Hello, Ada\"", Vm.Output.OutputsText);
        Assert.False(Vm.Output.IsFailure);
        Assert.NotEmpty(Vm.Output.ExecutionId);
        Assert.Equal(["who"], _studio.Dialogs.LastPrompts.Select(p => p.Name));
        Assert.Equal("\"World\"", _studio.Dialogs.LastPrompts[0].DefaultJson);
        Assert.All(Vm.Root.DescendantsAndSelf(), n => Assert.Equal(NodeRunState.Completed, n.RunState));
        var entry = Assert.Single(Vm.Log.Entries, e => e.Message == "Hello, Ada");
        Assert.Equal("say", entry.Node);
        Assert.False(Vm.IsRunning);
    }

    [Fact]
    public async Task Run_BlankArgument_UsesTheDefault()
    {
        Vm.Edit(_ => Drafts.Sample(), "load");
        _studio.Dialogs.ArgumentAnswers = new Dictionary<string, string> { ["who"] = " " };

        await Vm.RunCommand.ExecuteAsync(null);

        Assert.Equal("result = \"Hello, World\"", Vm.Output.OutputsText);
    }

    [Fact]
    public async Task Run_ArgumentPromptCancelled_DoesNotRun()
    {
        Vm.Edit(_ => Drafts.Sample(), "load");
        _studio.Dialogs.ArgumentAnswers = null;

        await Vm.RunCommand.ExecuteAsync(null);

        Assert.Equal("No run yet.", Vm.Output.Status);
    }

    [Fact]
    public async Task Run_WithBadArgumentText_ReportsIt()
    {
        Vm.Edit(d => d with { Arguments = [new ArgumentDraft("n", "In", "Int", Required: true)] }, "load");
        _studio.Dialogs.ArgumentAnswers = new Dictionary<string, string> { ["n"] = "many" };

        await Vm.RunCommand.ExecuteAsync(null);

        Assert.Equal("Not run", Vm.Output.Status);
        Assert.Contains("'n'", Vm.Output.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_InvalidWorkflow_IsNotStarted()
    {
        Vm.AddActivityCommand.Execute("Core.Throw"); // "message" missing

        await Vm.RunCommand.ExecuteAsync(null);

        Assert.Equal("Not run", Vm.Output.Status);
        Assert.Contains("1 error", Vm.Output.ErrorText, StringComparison.Ordinal);
        Assert.Equal(NodeRunState.None, Vm.Root.RunState);
    }

    [Fact]
    public async Task Run_Failure_HighlightsTheFailedNode()
    {
        Vm.Edit(d => d with { Root = d.Root with { Children = [Node("say", "Core.Log", "message", "'before'"), Node("boom", "Core.Throw", "message", "'broken'"), Node("never", "Core.Log", "message", "'after'")] } }, "load");

        await Vm.RunCommand.ExecuteAsync(null);

        Assert.Equal("Failed", Vm.Output.Status);
        Assert.True(Vm.Output.IsFailure);
        Assert.Contains("broken", Vm.Output.ErrorText, StringComparison.Ordinal);
        Assert.Contains("'boom'", Vm.Output.ErrorText, StringComparison.Ordinal);
        Assert.Equal(NodeRunState.Completed, NodeById("say").RunState);
        Assert.Equal(NodeRunState.Failed, NodeById("boom").RunState);
        Assert.Equal(NodeRunState.None, NodeById("never").RunState);

        // Run states survive edits (the designer is rebuilt) until the next run.
        Vm.Edit(d => d with { Name = "renamed" }, "rename");
        Assert.Equal(NodeRunState.Failed, NodeById("boom").RunState);
    }

    [Fact]
    public async Task Stop_CancelsTheRun_AndHighlightsTheRunningNode()
    {
        Vm.Edit(d => d with { Root = d.Root with { Children = [Node("wait", "Core.Delay", "milliseconds", "60000", literal: true)] } }, "load");
        var running = WaitForRunState("wait", NodeRunState.Running);

        var run = Vm.RunCommand.ExecuteAsync(null);
        await running.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(Vm.IsRunning);
        Assert.True(Vm.StopCommand.CanExecute(null));
        Vm.StopCommand.Execute(null);
        await run.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Cancelled", Vm.Output.Status);
        Assert.False(Vm.IsRunning);
        Assert.False(Vm.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task RunTimeout_StopsALongRun()
    {
        Vm.Edit(d => d with { Root = d.Root with { Children = [Node("wait", "Core.Delay", "milliseconds", "60000", literal: true)] } }, "load");
        Vm.RunTimeout = TimeSpan.FromSeconds(5);
        var running = WaitForRunState("wait", NodeRunState.Running);

        var run = Vm.RunCommand.ExecuteAsync(null);
        await running.WaitAsync(TestContext.Current.CancellationToken);
        _studio.Time.Advance(TimeSpan.FromSeconds(6));
        await run.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal("TimedOut", Vm.Output.Status);
    }

    [Fact]
    public async Task Run_ClearsPreviousRunStates()
    {
        Vm.Edit(d => d with { Root = d.Root with { Children = [Node("boom", "Core.Throw", "message", "'x'")] } }, "load");
        await Vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(NodeRunState.Failed, NodeById("boom").RunState);

        Vm.Edit(d => d with { Root = d.Root with { Children = [Node("boom", "Core.Log", "message", "'fine'")] } }, "fix");
        await Vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(NodeRunState.Completed, NodeById("boom").RunState);
    }

    private NodeViewModel NodeById(string id) => Vm.Root.DescendantsAndSelf().Single(n => n.Id == id);

    private Task WaitForRunState(string id, NodeRunState state)
    {
        var node = NodeById(id);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NodeViewModel.RunState) && node.RunState == state)
            {
                reached.TrySetResult();
            }
        };
        return reached.Task;
    }

    private static NodeDraft Node(string id, string type, string property, string value, bool literal = false) =>
        new(id, type) { Properties = [new PropertyEntry(property, new ScalarValue(value, literal))] };
}

internal static class StudioViewModelTestExtensions
{
    public static void VariablesCommandAdd(this StudioViewModel vm) => vm.Variables.AddCommand.Execute(null);
}
