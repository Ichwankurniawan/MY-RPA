using Microsoft.Extensions.DependencyInjection;
using MyRPA.Activities.BuiltIn;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;

namespace MyRPA.Activities.Tests;

public sealed class ActivityCatalogTests
{
    private sealed class Custom : IActivity
    {
        public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context) => ActivityResult.CompletedTask;
    }

    private static ActivityDescriptor Descriptor(string name) => new(new ActivityTypeName(name), name, "Test");

    private static ServiceProvider Build(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection().AddLogging();
        configure(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void AddMyRpaActivities_RegistersAllBuiltIns_SortedByName()
    {
        using var provider = Build(s => s.AddMyRpaActivities());

        Assert.Equal(
            [
                "Core.Assign", "Core.Collection.Filter", "Core.Collection.Find", "Core.Collection.Merge", "Core.Collection.Sort",
                "Core.Date.Add", "Core.Date.Difference", "Core.Date.Format", "Core.Date.Parse", "Core.Decision", "Core.Delay",
                "Core.DoWhile", "Core.Flowchart", "Core.ForEach", "Core.If", "Core.InvokeWorkflow", "Core.Json.Parse",
                "Core.Json.Serialize", "Core.Log", "Core.Sequence", "Core.State", "Core.StateMachine", "Core.Switch",
                "Core.Text.Join", "Core.Text.Match", "Core.Text.Replace", "Core.Text.Split", "Core.Throw", "Core.TryCatch",
                "Core.While",
            ],
            provider.GetRequiredService<IActivityCatalog>().Descriptors.Select(d => d.TypeName.Value));
    }

    [Fact]
    public void AddMyRpaActivities_IsIdempotent_AndCatalogIsAlsoTheFactory()
    {
        using var provider = Build(s => s.AddMyRpaActivities().AddMyRpaActivities());

        Assert.Equal(30, provider.GetRequiredService<IActivityCatalog>().Descriptors.Count);
        Assert.Same(provider.GetRequiredService<IActivityCatalog>(), provider.GetRequiredService<IActivityFactory>());
    }

    [Fact]
    public void AddActivity_RegistersCustomActivityExplicitly()
    {
        using var provider = Build(s => s.AddMyRpaActivities().AddActivity<Custom>(Descriptor("Acme.Custom")));
        var catalog = provider.GetRequiredService<ActivityCatalog>();

        Assert.True(catalog.TryGet(new ActivityTypeName("Acme.Custom"), out _));
        using var scope = provider.CreateScope();
        Assert.IsType<Custom>(catalog.Create(new ActivityTypeName("Acme.Custom"), scope.ServiceProvider));
        Assert.IsType<SequenceActivity>(catalog.Create(SequenceActivity.Descriptor.TypeName, scope.ServiceProvider));
    }

    [Fact]
    public void DuplicateTypeNames_AreAConfigurationError()
    {
        using var provider = Build(s => s.AddMyRpaActivities().AddActivity<Custom>(Descriptor("Core.Sequence")));

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IActivityCatalog>());
        Assert.Contains("Core.Sequence", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_UnknownType_Throws()
    {
        using var provider = Build(s => s.AddMyRpaActivities());
        var factory = provider.GetRequiredService<IActivityFactory>();

        Assert.Throws<InvalidOperationException>(() => factory.Create(new ActivityTypeName("Core.Missing"), provider));
    }

    [Fact]
    public void Registration_RejectsNonActivityTypes() =>
        Assert.Throws<ArgumentException>(() => new ActivityRegistration(Descriptor("Test.X"), typeof(string)));

    [Fact]
    public void BuiltInDescriptors_DeclareTheirSchemas()
    {
        Assert.True(SequenceActivity.Descriptor.AllowsChildren);
        Assert.True(IfActivity.Descriptor.FindSlot("then")!.IsRequired);
        Assert.NotNull(SwitchActivity.Descriptor.FindSlot("case:anything"));
        Assert.Equal(["body"], ForEachActivity.Descriptor.FindProperty("itemVariable")!.ScopeSlots);
        Assert.Equal(ActivityPropertyKind.AssignmentTarget, AssignActivity.Descriptor.FindProperty("to")!.Kind);
        Assert.Contains("Warning", LogActivity.Descriptor.FindProperty("level")!.AllowedValues);
        Assert.Equal(ActivityPropertyKind.AssignmentTargetMap, InvokeWorkflowActivity.Descriptor.FindProperty("outputs")!.Kind);
    }
}
