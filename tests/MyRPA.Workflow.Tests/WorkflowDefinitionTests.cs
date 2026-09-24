using MyRPA.Core.Activities;
using MyRPA.Core.Identifiers;
using MyRPA.Workflow.Expressions;
using MyRPA.Workflow.Values;

namespace MyRPA.Workflow.Tests;

/// <summary>Domain invariants of the model (user-facing validation is tested in <see cref="WorkflowLoaderTests"/>).</summary>
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
        Assert.Empty(workflow.Arguments);
        Assert.Empty(workflow.Variables);
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
    public void Constructor_DuplicateNodeIds_IncludingSlots_Throws()
    {
        var slotted = new NodeDefinition(
            new NodeId("if"),
            new ActivityTypeName("Core.If"),
            slots: [new("then", Node("a"))]);
        var root = Node("root", Node("a"), slotted);

        var ex = Assert.Throws<ArgumentException>(() => new WorkflowDefinition(new WorkflowId("w"), "W", "1", root));
        Assert.Contains("'a'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_ArgumentAndVariableNamesMustBeUnique()
    {
        Assert.Throws<ArgumentException>(() => new WorkflowDefinition(
            new WorkflowId("w"), "W", "1", Node("r"),
            arguments: [new("x", ArgumentDirection.In, WorkflowDataType.Int)],
            variables: [new("x", WorkflowDataType.String)]));
    }

    [Fact]
    public void ArgumentDefinition_Invariants()
    {
        Assert.Throws<ArgumentException>(() => new ArgumentDefinition("1x", ArgumentDirection.In, WorkflowDataType.Int));
        Assert.Throws<ArgumentException>(() => new ArgumentDefinition("x", ArgumentDirection.Out, WorkflowDataType.Int, isRequired: true));
        Assert.Throws<ArgumentException>(() => new ArgumentDefinition("x", ArgumentDirection.Out, WorkflowDataType.Int, defaultValue: 1L));
        Assert.Throws<ArgumentException>(() => new ArgumentDefinition("x", ArgumentDirection.In, WorkflowDataType.Int, defaultValue: "one"));

        var widened = new ArgumentDefinition("price", ArgumentDirection.InOut, WorkflowDataType.Decimal, defaultValue: 2L);
        Assert.Equal(2m, widened.DefaultValue);
        Assert.True(widened.IsInput);
        Assert.True(widened.IsOutput);
    }

    [Theory]
    [InlineData("valid_name", true)]
    [InlineData("_x1", true)]
    [InlineData("1x", false)]
    [InlineData("has-dash", false)]
    [InlineData("null", false)]
    [InlineData("", false)]
    public void WorkflowNames_Rules(string name, bool valid) => Assert.Equal(valid, WorkflowNames.IsValid(name));

    [Fact]
    public void Node_ChildrenAndPropertiesAreCopiedAndReadOnly()
    {
        var children = new List<NodeDefinition> { Node("a") };
        var properties = new Dictionary<string, PropertyValue> { ["value"] = new ExpressionPropertyValue(WorkflowExpression.Parse("1")) };
        var parent = new NodeDefinition(new NodeId("root"), new ActivityTypeName("Core.Sequence"), children: children, properties: properties);
        children.Add(Node("b"));
        properties.Clear();

        Assert.Single(parent.Children);
        Assert.Single(parent.Properties);
        Assert.False(parent.Children is List<NodeDefinition>);
    }

    [Fact]
    public void Node_NullChild_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            new NodeDefinition(new NodeId("r"), new ActivityTypeName("Core.Sequence"), children: [null!]));

    [Fact]
    public void Node_DescendantsAndSelf_IsDepthFirstPreOrder_ChildrenBeforeSlots()
    {
        var node = new NodeDefinition(
            new NodeId("root"),
            new ActivityTypeName("Core.Sequence"),
            children: [Node("a", Node("a1")), Node("b")],
            slots: [new("extra", Node("s"))]);

        Assert.Equal(["root", "a", "a1", "b", "s"], node.DescendantsAndSelf().Select(n => n.Id.Value));
    }
}
