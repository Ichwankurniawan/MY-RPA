using MyRPA.Core.Activities;
using MyRPA.Workflow.Serialization;
using MyRPA.Workflow.Validation;

namespace MyRPA.Workflow.Tests;

/// <summary>Format 1.1: transitions, layout and graph containers (ADR-0037).</summary>
public sealed class GraphWorkflowTests
{
    private readonly WorkflowLoader _loader = new(new TestCatalog());

    private const string Flowchart = """
        {
          "schemaVersion": "1.1",
          "id": "retry",
          "name": "Retry",
          "version": "1.0.0",
          "variables": [ { "name": "n", "type": "Int", "default": 0 } ],
          "root": {
            "id": "flow",
            "type": "Core.Flowchart",
            "properties": { "maxSteps": 100 },
            "children": [
              {
                "id": "try",
                "type": "Core.Assign",
                "properties": { "to": "n", "value": "n + 1" },
                "layout": { "x": 80, "y": 60.5 },
                "transitions": [ { "to": "decide" } ]
              },
              {
                "id": "decide",
                "type": "Core.Decision",
                "transitions": [ { "to": "try", "when": "n < 3", "label": "again" }, { "to": "done", "when": true } ]
              },
              { "id": "done", "type": "Core.Log", "properties": { "message": "'finished'" } }
            ]
          }
        }
        """;

    private WorkflowLoadResult Load(string json) => _loader.Load(json);

    /// <summary>The flowchart above with one text replaced.</summary>
    private WorkflowLoadResult LoadWith(string find, string replace)
    {
        Assert.Contains(find, Flowchart, StringComparison.Ordinal);
        return Load(Flowchart.Replace(find, replace, StringComparison.Ordinal));
    }

    private static void AssertError(WorkflowLoadResult result, string code, string path)
    {
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == code && e.Path == path);
    }

    [Fact]
    public void Flowchart_LoadsTransitionsAndLayout()
    {
        var result = Load(Flowchart);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        var workflow = result.Workflow!;
        Assert.Equal(WorkflowSchemaVersion.Graphs, workflow.SchemaVersion);
        var (start, decide) = (workflow.Root.Children[0], workflow.Root.Children[1]);
        Assert.Equal(new NodeLayout(80, 60.5), start.Layout);
        Assert.Equal("decide", Assert.Single(start.Transitions).To.Value);
        Assert.Null(start.Transitions[0].When);
        Assert.Equal(["try", "done"], decide.Transitions.Select(t => t.To.Value));
        Assert.Equal("n < 3", decide.Transitions[0].When!.Source);
        Assert.Equal("again", decide.Transitions[0].Label);
        Assert.True(decide.Transitions[1].When!.IsJsonLiteral);
    }

    [Fact]
    public void Writer_RoundTripsTransitionsAndLayout()
    {
        var first = WorkflowJsonWriter.Write(Load(Flowchart).Workflow!);
        var reloaded = Load(first);

        Assert.True(reloaded.IsValid, string.Join(Environment.NewLine, reloaded.Diagnostics));
        Assert.Equal(first, WorkflowJsonWriter.Write(reloaded.Workflow!));
        Assert.Contains("\"schemaVersion\": \"1.1\"", first, StringComparison.Ordinal);
        Assert.Contains("\"layout\": {", first, StringComparison.Ordinal);
        Assert.Contains("\"when\": true", first, StringComparison.Ordinal);
        Assert.Contains("\"label\": \"again\"", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Writer_KeepsATreeWorkflowAt10()
    {
        var json = """{ "schemaVersion": "1.0", "id": "w", "name": "W", "version": "1", "root": { "id": "main", "type": "Core.Sequence" } }""";

        Assert.Contains("\"schemaVersion\": \"1.0\"", WorkflowJsonWriter.Write(Load(json).Workflow!), StringComparison.Ordinal);
    }

    [Fact]
    public void TransitionTarget_MustBeASiblingStep() =>
        AssertError(LoadWith("""{ "to": "done", "when": true }""", """{ "to": "flow" }"""), DiagnosticCodes.InvalidTransitionTarget, "$.root.children[1].transitions[1].to");

    [Fact]
    public void TransitionTarget_MissingIsReported() =>
        AssertError(LoadWith("""{ "to": "decide" }""", """{ "label": "x" }"""), DiagnosticCodes.MissingField, "$.root.children[0].transitions[0].to");

    [Fact]
    public void Transitions_OutsideAGraphContainer_AreRefused()
    {
        var result = Load("""
            { "schemaVersion": "1.1", "id": "w", "name": "W", "version": "1",
              "root": { "id": "main", "type": "Core.Sequence", "children": [
                { "id": "a", "type": "Core.Log", "properties": { "message": "1" }, "transitions": [ { "to": "b" } ] },
                { "id": "b", "type": "Core.Log", "properties": { "message": "2" } } ] } }
            """);

        AssertError(result, DiagnosticCodes.TransitionsNotAllowed, "$.root.children[0].transitions");
    }

    [Fact]
    public void Transitions_OnTheRoot_AreRefused() =>
        AssertError(
            Load("""{ "schemaVersion": "1.1", "id": "w", "name": "W", "version": "1", "root": { "id": "main", "type": "Core.Sequence", "transitions": [] } }"""),
            DiagnosticCodes.TransitionsNotAllowed,
            "$.root.transitions");

    [Fact]
    public void GraphContainer_WithoutSteps_IsRefused() =>
        AssertError(
            Load("""{ "schemaVersion": "1.1", "id": "w", "name": "W", "version": "1", "root": { "id": "flow", "type": "Core.Flowchart" } }"""),
            DiagnosticCodes.InvalidGraphSteps,
            "$.root.children");

    [Fact]
    public void UnreachableStep_IsAWarning_AndTheWorkflowStaysValid()
    {
        var result = LoadWith("""{ "to": "done", "when": true }""", """{ "to": "try" }""");

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Diagnostics));
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreachableStep, warning.Code);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("$.root.children[2]", warning.Path);
        Assert.Equal("done", warning.NodeId);
    }

    [Theory]
    [InlineData("\"n < \"", DiagnosticCodes.InvalidExpression)]
    [InlineData("\"missing > 1\"", DiagnosticCodes.UnknownName)]
    [InlineData("[]", DiagnosticCodes.InvalidPropertyValue)]
    public void TransitionCondition_IsValidatedLikeAnExpression(string when, string code) =>
        AssertError(LoadWith("\"when\": \"n < 3\"", $"\"when\": {when}"), code, "$.root.children[1].transitions[0].when");

    [Fact]
    public void Layout_RequiresFiniteNumbers()
    {
        AssertError(LoadWith("\"x\": 80", "\"x\": 1e400"), DiagnosticCodes.InvalidGraphNode, "$.root.children[0].layout.x");
        AssertError(LoadWith("\"x\": 80", "\"x\": \"80\""), DiagnosticCodes.WrongFieldType, "$.root.children[0].layout.x");
        AssertError(LoadWith("\"x\": 80, ", string.Empty), DiagnosticCodes.MissingField, "$.root.children[0].layout.x");
        AssertError(LoadWith("{ \"x\": 80, \"y\": 60.5 }", "[]"), DiagnosticCodes.WrongFieldType, "$.root.children[0].layout");
    }

    [Fact]
    public void UnknownTransitionField_IsAWarning()
    {
        var result = LoadWith("\"label\": \"again\"", "\"label\": \"again\", \"color\": \"red\"");

        Assert.True(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.UnknownField && d.Path == "$.root.children[1].transitions[0].color");
    }

    [Fact]
    public void GraphData_InA10File_NeedsSchema11()
    {
        var result = LoadWith("\"schemaVersion\": \"1.1\"", "\"schemaVersion\": \"1.0\"");

        Assert.False(result.IsValid);
        Assert.Equal(
            ["$.root.type", "$.root.children[0].transitions", "$.root.children[0].layout", "$.root.children[1].transitions"],
            result.Errors.Where(e => e.Code == DiagnosticCodes.RequiresNewerSchema).Select(e => e.Path));
    }

    [Fact]
    public void WorkflowDefinition_WithGraphData_DefaultsTo11_AndRefuses10()
    {
        var step = new NodeDefinition(new("a"), new("Core.Decision"), layout: new NodeLayout(1, 2));
        var root = new NodeDefinition(new("flow"), new("Core.Flowchart"), children: [step]);

        Assert.Equal(WorkflowSchemaVersion.Graphs, new WorkflowDefinition(new("w"), "W", "1", root).SchemaVersion);
        Assert.Throws<ArgumentException>(() => new WorkflowDefinition(new("w"), "W", "1", root, WorkflowSchemaVersion.Initial));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NodeLayout(double.NaN, 0));
    }

    [Fact]
    public void StateMachine_HoldsStatesOnly_AndAFinalStateHasNoTransitions()
    {
        var result = Load("""
            { "schemaVersion": "1.1", "id": "w", "name": "W", "version": "1",
              "root": { "id": "machine", "type": "Core.StateMachine", "children": [
                { "id": "start", "type": "Core.State", "transitions": [ { "to": "done" }, { "to": "stray" } ] },
                { "id": "stray", "type": "Core.Log", "properties": { "message": "1" } },
                { "id": "done", "type": "Core.State", "properties": { "final": true }, "transitions": [ { "to": "start" } ] },
                { "id": "also-final", "type": "Core.State", "properties": { "final": "true" }, "transitions": [ { "to": "start" } ] },
                { "id": "maybe", "type": "Core.State", "properties": { "final": "1 > 2" }, "transitions": [ { "to": "start" } ] } ] } }
            """);

        Assert.Equal(
            [(DiagnosticCodes.InvalidGraphSteps, "$.root.children[1].type"), (DiagnosticCodes.InvalidGraphNode, "$.root.children[2].transitions"), (DiagnosticCodes.InvalidGraphNode, "$.root.children[3].transitions")],
            result.Errors.Select(e => (e.Code, e.Path)));
    }

    [Fact]
    public void Descriptor_GraphLayout_RequiresChildren() =>
        Assert.Throws<ArgumentException>(() => new ActivityDescriptor(new("Acme.Graph"), "Graph", "Test", childLayout: ActivityChildLayout.Graph));

    [Fact]
    public void CatalogSnapshot_WritesVersion11WithChildLayout_AndReadsItBack()
    {
        var json = ActivityCatalogJson.Write(new TestCatalog().Descriptors);
        var snapshot = ActivityCatalogJson.Read(json);

        Assert.Contains("\"catalogVersion\": \"1.1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"childLayout\": \"Graph\"", json, StringComparison.Ordinal);
        Assert.True(snapshot.TryGet(new("Core.Flowchart"), out var flowchart));
        Assert.Equal(ActivityChildLayout.Graph, flowchart.ChildLayout);
        Assert.True(snapshot.TryGet(new("Core.Sequence"), out var sequence));
        Assert.Equal(ActivityChildLayout.List, sequence.ChildLayout);
    }

    [Fact]
    public void CatalogSnapshot_Version10_IsStillRead_AsListLayout()
    {
        var snapshot = ActivityCatalogJson.Read("""
            { "catalogVersion": "1.0", "activities": [ { "type": "A.B", "displayName": "d", "category": "c", "allowsChildren": true, "properties": [], "slots": [] } ] }
            """);

        Assert.Equal(ActivityChildLayout.List, Assert.Single(snapshot.Descriptors).ChildLayout);
    }

    [Fact]
    public void CatalogSnapshot_UnknownChildLayout_IsRejected()
    {
        var error = Assert.Throws<FormatException>(() => ActivityCatalogJson.Read("""
            { "catalogVersion": "1.1", "activities": [ { "type": "A.B", "displayName": "d", "category": "c", "allowsChildren": true, "childLayout": "Tree", "properties": [], "slots": [] } ] }
            """));

        Assert.Contains("$.activities[0].childLayout", error.Message, StringComparison.Ordinal);
    }
}
