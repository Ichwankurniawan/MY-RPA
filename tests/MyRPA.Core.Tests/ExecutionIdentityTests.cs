using MyRPA.Core.Diagnostics;
using MyRPA.Core.Identifiers;

namespace MyRPA.Core.Tests;

public sealed class ExecutionIdentityTests
{
    [Fact]
    public void CreateNew_WithoutCorrelation_GeneratesOne()
    {
        var identity = ExecutionIdentity.CreateNew();
        Assert.NotNull(identity.CorrelationId);
        Assert.Null(identity.WorkflowId);
        Assert.Null(identity.NodeId);
    }

    [Fact]
    public void CreateNew_WithCorrelation_KeepsIt()
    {
        var correlation = new CorrelationId("job-42");
        Assert.Same(correlation, ExecutionIdentity.CreateNew(correlation).CorrelationId);
    }

    [Fact]
    public void ToTags_MinimalIdentity_ContainsExecutionAndCorrelationOnly()
    {
        var identity = ExecutionIdentity.CreateNew(new CorrelationId("c-1"));

        var tags = identity.ToTags().ToDictionary(t => t.Key, t => t.Value);

        Assert.Equal(2, tags.Count);
        Assert.Equal(identity.ExecutionId.ToString(), tags[DiagnosticNames.ExecutionIdKey]);
        Assert.Equal("c-1", tags[DiagnosticNames.CorrelationIdKey]);
    }

    [Fact]
    public void ForWorkflowAndForNode_AddTagsWithoutChangingOriginal()
    {
        var original = ExecutionIdentity.CreateNew();

        var node = original.ForWorkflow(new WorkflowId("wf")).ForNode(new NodeId("n1"));
        var tags = node.ToTags().ToDictionary(t => t.Key, t => t.Value);

        Assert.Equal("wf", tags[DiagnosticNames.WorkflowIdKey]);
        Assert.Equal("n1", tags[DiagnosticNames.NodeIdKey]);
        Assert.Equal(original.ExecutionId, node.ExecutionId);
        Assert.Null(original.WorkflowId);
        Assert.Null(original.NodeId);
    }

    [Fact]
    public void DiagnosticNames_AreStableContract()
    {
        // Changing these breaks dashboards/queries; requires an ADR (ADR-0006).
        Assert.Equal("MyRPA.Runtime", DiagnosticNames.RuntimeActivitySource);
        Assert.Equal("myrpa.execution.id", DiagnosticNames.ExecutionIdKey);
        Assert.Equal("myrpa.workflow.id", DiagnosticNames.WorkflowIdKey);
        Assert.Equal("myrpa.node.id", DiagnosticNames.NodeIdKey);
        Assert.Equal("myrpa.correlation.id", DiagnosticNames.CorrelationIdKey);
    }
}
