using MyRPA.Core.Activities;
using MyRPA.Core.Identifiers;

namespace MyRPA.Workflow.Tests;

public sealed class WorkflowDefinitionTests
{
    private static NodeDefinition Node(string id, params NodeDefinition[] children) =>
        new(new NodeId(id), new ActivityTypeName("Core.Sequence"), children: children);

    [Fact]
    public void Constructor_DefaultsSchemaVersionToCurrent()
    {
        var workflow = new WorkflowDefinition(new WorkflowId("hello"), "Hello", "1.0.0", Node("root"));

        Assert.Equal(WorkflowSchemaVersion.Current, workflow.SchemaVersion);
        Assert.Equal("1.0.0", workflow.Version);
    }

    [Fact]
    public void Constructor_KeepsContentVersionSeparateFromSchemaVersion()
    {
        var workflow = new WorkflowDefinition(
            new WorkflowId("hello"), "Hello", "7.3.1", Node("root"), new WorkflowSchemaVersion(1, 2));

        Assert.Equal("7.3.1", workflow.Version);
        Assert.Equal("1.2", workflow.SchemaVersion.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Constructor_BlankNameOrVersion_Throws(string blank)
    {
        Assert.Throws<ArgumentException>(() => new WorkflowDefinition(new WorkflowId("w"), blank, "1", Node("r")));
        Assert.Throws<ArgumentException>(() => new WorkflowDefinition(new WorkflowId("w"), "W", blank, Node("r")));
    }

    [Fact]
    public void Constructor_DuplicateNodeIds_Throws()
    {
        var root = Node("root", Node("a"), Node("b", Node("a")));

        var ex = Assert.Throws<ArgumentException>(() => new WorkflowDefinition(new WorkflowId("w"), "W", "1", root));
        Assert.Contains("'a'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Node_ChildrenAreCopiedAndReadOnly()
    {
        var children = new List<NodeDefinition> { Node("a") };
        var parent = Node("root", [.. children]);
        children.Add(Node("b"));

        Assert.Single(parent.Children);
        Assert.False(parent.Children is List<NodeDefinition>);
    }

    [Fact]
    public void Node_NullChild_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            new NodeDefinition(new NodeId("r"), new ActivityTypeName("Core.Sequence"), children: [null!]));

    [Fact]
    public void Node_DescendantsAndSelf_IsDepthFirstPreOrder()
    {
        var root = Node("root", Node("a", Node("a1")), Node("b"));

        var order = root.DescendantsAndSelf().Select(n => n.Id.Value);

        Assert.Equal(["root", "a", "a1", "b"], order);
    }
}
