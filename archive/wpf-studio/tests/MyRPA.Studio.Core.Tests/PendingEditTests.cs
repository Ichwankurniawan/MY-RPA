using MyRPA.Studio.Documents;
using MyRPA.Studio.Services;
using MyRPA.Studio.ViewModels;

namespace MyRPA.Studio.Tests;

/// <summary>
/// Values typed into an editor are committed on Enter or when focus leaves it. Save, Run and closing can happen without
/// either (keyboard shortcuts, the window's close button), so they must apply pending values first (Phase 5 review).
/// </summary>
public sealed class PendingEditTests : IDisposable
{
    private readonly StudioHarness _studio = new();

    private StudioViewModel Vm => _studio.ViewModel;

    public void Dispose() => _studio.Dispose();

    [Fact]
    public async Task Save_AppliesATypedButUncommittedPropertyValue()
    {
        Vm.AddActivityCommand.Execute("Core.Log");
        Editor("message").Text = "'typed, not committed'";
        Assert.Null(Vm.Document.Root.Children[0].Property("message")); // still pending
        _studio.Dialogs.SavePath = "pending.json";

        await Vm.SaveCommand.ExecuteAsync(null);

        var saved = DraftJson.Read(_studio.Storage.Files[Path.GetFullPath("pending.json")]);
        Assert.Equal(new ScalarValue("'typed, not committed'"), saved.Root.Children[0].Property("message"));
        Assert.False(Vm.IsDirty);
        Assert.Equal("'typed, not committed'", Editor("message").Text);
    }

    [Fact]
    public async Task SaveAs_AppliesATypedButUncommittedWorkflowField()
    {
        Vm.Properties.WorkflowName = "Typed name";
        _studio.Dialogs.SavePath = "named.json";

        await Vm.SaveAsCommand.ExecuteAsync(null);

        Assert.Equal("Typed name", DraftJson.Read(_studio.Storage.Files[Path.GetFullPath("named.json")]).Name);
    }

    [Fact]
    public async Task Run_UsesATypedButUncommittedPropertyValue()
    {
        Vm.AddActivityCommand.Execute("Core.Log");
        Editor("message").Text = "'typed before F5'";

        await Vm.RunCommand.ExecuteAsync(null);

        Assert.Equal("Succeeded", Vm.Output.Status);
        Assert.Contains(Vm.Log.Entries, e => e.Message == "typed before F5");
    }

    [Fact]
    public async Task Run_UsesATypedButUncommittedArgumentDefault()
    {
        Vm.Edit(_ => Drafts.Sample(), "load");
        Vm.Arguments.Rows.Single(r => r.Name == "who").DefaultJson = "\"Typed\"";

        await Vm.RunCommand.ExecuteAsync(null);

        Assert.Equal("result = \"Hello, Typed\"", Vm.Output.OutputsText);
    }

    [Fact]
    public async Task ConfirmClose_WithOnlyAnUncommittedValue_AsksToSave()
    {
        Assert.False(Vm.IsDirty);
        Vm.Properties.WorkflowName = "Typed name";
        _studio.Dialogs.SaveChoice = UnsavedChangesChoice.Cancel;

        Assert.False(await Vm.ConfirmCloseAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, _studio.Dialogs.SaveQuestions);
        Assert.True(Vm.IsDirty);
        Assert.Equal("Typed name", Vm.Document.Name);
    }

    [Fact]
    public async Task ConfirmClose_SavingAnUncommittedValue_WritesIt()
    {
        Vm.VariablesCommandAdd();
        Vm.Variables.Rows[0].Name = "typedVariable";
        _studio.Dialogs.SaveChoice = UnsavedChangesChoice.Save;
        _studio.Dialogs.SavePath = "closing.json";

        Assert.True(await Vm.ConfirmCloseAsync(TestContext.Current.CancellationToken));

        Assert.Equal("typedVariable", DraftJson.Read(_studio.Storage.Files[Path.GetFullPath("closing.json")]).Variables.Single().Name);
    }

    [Fact]
    public async Task ConfirmClose_WithNothingPending_DoesNotChangeTheDocument()
    {
        Vm.AddActivityCommand.Execute("Core.Log");
        Editor("message").Text = "'x'";
        Editor("message").Commit();
        _studio.Dialogs.SavePath = "clean.json";
        await Vm.SaveCommand.ExecuteAsync(null);
        var saved = Vm.Document;

        Assert.True(await Vm.ConfirmCloseAsync(TestContext.Current.CancellationToken));

        Assert.Same(saved, Vm.Document);
        Assert.Equal(0, _studio.Dialogs.SaveQuestions);
    }

    [Fact]
    public void PendingValuesInSeveralPanes_AreAppliedTogether_AsOneUndoStep()
    {
        Vm.VariablesCommandAdd();
        Vm.AddActivityCommand.Execute("Core.Log");
        Vm.Variables.Rows[0].Name = "typedVariable";
        Editor("message").Text = "'typed'";
        Vm.Properties.NodeDisplayName = "Typed title";

        Assert.True(Vm.CommitPendingEdits());

        Assert.Equal("typedVariable", Vm.Document.Variables[0].Name);
        var log = Vm.Document.Root.Children[0];
        Assert.Equal(new ScalarValue("'typed'"), log.Property("message"));
        Assert.Equal("Typed title", log.DisplayName);

        Vm.UndoCommand.Execute(null);
        Assert.Equal("variable1", Vm.Document.Variables[0].Name);
        Assert.Null(Vm.Document.Root.Children[0].Property("message"));
    }

    [Fact]
    public async Task RefusedPendingValue_CancelsSave_AndKeepsTheTypedText()
    {
        Vm.VariablesCommandAdd();
        Vm.Variables.Rows[0].DefaultJson = "{ not json";
        _studio.Dialogs.SavePath = "refused.json";

        await Vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(_studio.Storage.Files);
        Assert.Single(_studio.Dialogs.Errors);
        Assert.Equal("{ not json", Vm.Variables.Rows[0].DefaultJson);
    }

    [Fact]
    public async Task RefusedPendingValue_CancelsRunAndClose()
    {
        Vm.VariablesCommandAdd();
        Vm.Variables.Rows[0].DefaultJson = "{ not json";
        _studio.Dialogs.SaveChoice = UnsavedChangesChoice.Discard;

        await Vm.RunCommand.ExecuteAsync(null);
        Assert.Equal("No run yet.", Vm.Output.Status);

        Assert.False(await Vm.ConfirmCloseAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, _studio.Dialogs.Errors.Count);
        Assert.Equal(0, _studio.Dialogs.SaveQuestions);
    }

    private PropertyEditorViewModel Editor(string name) => Vm.Properties.Editors.Single(e => e.Name == name);
}
