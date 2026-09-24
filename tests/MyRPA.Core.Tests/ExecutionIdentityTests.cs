using MyRPA.Core.Diagnostics;
using MyRPA.Core.Identifiers;

namespace MyRPA.Core.Tests;

public sealed class ExecutionIdentityTests
{
    [Fact]
    public void CreateNew_WithoutCorrelation_UsesGeneratorForBothIds()
    {
        var identity = ExecutionIdentity.CreateNew(new SequentialIdGenerator());

        Assert.Equal("00000001000000000000000000000000", identity.ExecutionId.ToString());
        Assert.Equal("corr-2", identity.CorrelationId.Value);
        Assert.Null(identity.WorkflowId);
        Assert.Null(identity.NodeId);
        Assert.Null(identity.ParentExecutionId);
    }

    [Fact]
    public void CreateNew_WithCorrelation_KeepsIt()
    {
        var correlation = new CorrelationId("job-42");
        Assert.Same(correlation, ExecutionIdentity.CreateNew(new SequentialIdGenerator(), correlation).CorrelationId);
    }

    [Fact]
    public void ToTags_MinimalIdentity_ContainsExecutionAndCorrelationOnly()
    {
        var identity = ExecutionIdentity.CreateNew(new SequentialIdGenerator(), new CorrelationId("c-1"));

        var tags = identity.ToTags().ToDictionary(t => t.Key, t => t.Value);

        Assert.Equal(2, tags.Count);
        Assert.Equal(identity.ExecutionId.ToString(), tags[DiagnosticNames.ExecutionIdKey]);
        Assert.Equal("c-1", tags[DiagnosticNames.CorrelationIdKey]);
    }

    [Fact]
    public void ForWorkflowAndForNode_AddTagsWithoutChangingOriginal()
    {
        var original = ExecutionIdentity.CreateNew(new SequentialIdGenerator());

        var node = original.ForWorkflow(new WorkflowId("wf")).ForNode(new NodeId("n1"));
        var tags = node.ToTags().ToDictionary(t => t.Key, t => t.Value);

        Assert.Equal("wf", tags[DiagnosticNames.WorkflowIdKey]);
        Assert.Equal("n1", tags[DiagnosticNames.NodeIdKey]);
        Assert.Equal(original.ExecutionId, node.ExecutionId);
        Assert.Null(original.WorkflowId);
        Assert.Null(original.NodeId);
    }

    [Fact]
    public void ForChildExecution_KeepsCorrelation_LinksParent_DropsNode()
    {
        var ids = new SequentialIdGenerator();
        var parent = ExecutionIdentity.CreateNew(ids).ForWorkflow(new WorkflowId("parent")).ForNode(new NodeId("invoke"));
        var childId = ids.NewExecutionId();

        var child = parent.ForChildExecution(childId, new WorkflowId("child"));
        var tags = child.ToTags().ToDictionary(t => t.Key, t => t.Value);

        Assert.Equal(childId, child.ExecutionId);
        Assert.Equal(parent.CorrelationId, child.CorrelationId);
        Assert.Equal(parent.ExecutionId, child.ParentExecutionId);
        Assert.Null(child.NodeId);
        Assert.Equal(parent.ExecutionId.ToString(), tags[DiagnosticNames.ParentExecutionIdKey]);
        Assert.Equal("child", tags[DiagnosticNames.WorkflowIdKey]);
    }

    [Fact]
    public void DiagnosticNames_AreStableContract()
    {
        // Changing these breaks dashboards/queries; requires an ADR (ADR-0006, ADR-0010).
        Assert.Equal("MyRPA.Runtime", DiagnosticNames.RuntimeActivitySource);
        Assert.Equal("MyRPA.Workflow.Log", DiagnosticNames.WorkflowLogCategory);
        Assert.Equal("workflow.execute", DiagnosticNames.WorkflowExecuteOperation);
        Assert.Equal("myrpa.execution.id", DiagnosticNames.ExecutionIdKey);
        Assert.Equal("myrpa.parent_execution.id", DiagnosticNames.ParentExecutionIdKey);
        Assert.Equal("myrpa.workflow.id", DiagnosticNames.WorkflowIdKey);
        Assert.Equal("myrpa.node.id", DiagnosticNames.NodeIdKey);
        Assert.Equal("myrpa.correlation.id", DiagnosticNames.CorrelationIdKey);
        Assert.Equal("myrpa.activity.type", DiagnosticNames.ActivityTypeKey);
        Assert.Equal("myrpa.outcome", DiagnosticNames.OutcomeKey);
    }
}
