using MyRPA.Workflow.Validation;
using MyRPA.Workflow.Values;

namespace MyRPA.Workflow.Tests;

public sealed class WorkflowLoaderTests
{
    private readonly WorkflowLoader _loader = new(new TestCatalog());

    private const string ValidWorkflow = """
        {
          "schemaVersion": "1.0",
          "id": "orders",
          "name": "Orders",
          "version": "2.1.0",
          "description": "Sums orders.",
          "arguments": [
            { "name": "items", "direction": "In", "type": "List", "required": true },
            { "name": "label", "direction": "In", "type": "String", "default": "total" },
            { "name": "total", "direction": "Out", "type": "Int" },
            { "name": "log", "direction": "InOut", "type": "String" }
          ],
          "variables": [ { "name": "sum", "type": "Int", "default": 0 } ],
          "root": {
            "id": "main", "type": "Core.Sequence", "displayName": "Main",
            "children": [
              { "id": "loop", "type": "Core.ForEach",
                "properties": { "items": "items", "itemVariable": "item" },
                "slots": { "body": { "id": "add", "type": "Core.Assign", "properties": { "to": "sum", "value": "sum + item" } } } },
              { "id": "check", "type": "Core.If", "properties": { "condition": "sum > 10" },
                "slots": {
                  "then": { "id": "big", "type": "Core.Log", "properties": { "message": "label + ' is big'", "level": "Warning" } },
                  "else": { "id": "small", "type": "Core.Log", "properties": { "message": 0 } } } },
              { "id": "out", "type": "Core.Assign", "properties": { "to": "total", "value": "sum" } }
            ]
          }
        }
        """;

    private WorkflowLoadResult Load(string json) => _loader.Load(json);

    private static string Minimal(string root, string arguments = "[]", string variables = "[]") =>
        $$"""{ "schemaVersion": "1.0", "id": "w", "name": "W", "version": "1", "arguments": {{arguments}}, "variables": {{variables}}, "root": {{root}} }""";

    private static void AssertSingleError(WorkflowLoadResult result, string code, string pathFragment)
    {
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal(code, error.Code);
        Assert.Contains(pathFragment, error.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_BuildsCompleteModel()
    {
        var result = Load(ValidWorkflow);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        var workflow = result.Workflow;
        Assert.Equal("orders", workflow.Id.Value);
        Assert.Equal("2.1.0", workflow.Version);
        Assert.Equal(WorkflowSchemaVersion.Current, workflow.SchemaVersion);
        Assert.Equal("Sums orders.", workflow.Description);

        Assert.Equal(4, workflow.Arguments.Count);
        Assert.True(workflow.Arguments[0].IsRequired);
        Assert.Equal("total", workflow.Arguments[1].DefaultValue);
        Assert.Equal(ArgumentDirection.Out, workflow.Arguments[2].Direction);
        Assert.Equal(ArgumentDirection.InOut, workflow.Arguments[3].Direction);
        Assert.Equal(0L, Assert.Single(workflow.Variables).DefaultValue);

        Assert.Equal("Main", workflow.Root.DisplayName);
        Assert.Equal(3, workflow.Root.Children.Count);
        var loop = workflow.Root.Children[0];
        Assert.IsType<NamePropertyValue>(loop.Properties["itemVariable"]);
        Assert.Equal("add", loop.Slots["body"].Id.Value);
        var check = workflow.Root.Children[1];
        Assert.Equal(["then", "else"], check.Slots.Keys);
        var literal = Assert.IsType<ExpressionPropertyValue>(check.Slots["else"].Properties["message"]);
        Assert.True(literal.Expression.IsJsonLiteral);
        Assert.Equal(["main", "loop", "add", "check", "big", "small", "out"], workflow.Root.DescendantsAndSelf().Select(n => n.Id.Value));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("{\"a\": 1,, }")]
    public void MalformedJson_IsReported(string json)
    {
        var result = Load(json);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.MalformedJson, error.Code);
    }

    [Fact]
    public void RootNotObject_IsReported() => AssertSingleError(Load("[1, 2]"), DiagnosticCodes.RootNotObject, "$");

    [Fact]
    public void MissingFields_AreAllReported()
    {
        var result = Load("""{ "schemaVersion": "1.0" }""");

        Assert.Equal(
            ["$.id", "$.name", "$.version", "$.root"],
            result.Errors.Where(e => e.Code == DiagnosticCodes.MissingField).Select(e => e.Path));
    }

    [Fact]
    public void WrongFieldTypes_AreReported()
    {
        var result = Load("""{ "schemaVersion": "1.0", "id": 5, "name": "W", "version": "1", "arguments": {}, "root": [] }""");

        Assert.Equal(["$.id", "$.arguments", "$.root"], result.Errors.Select(e => e.Path));
        Assert.All(result.Errors, e => Assert.Equal(DiagnosticCodes.WrongFieldType, e.Code));
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("1.1")]
    public void UnsupportedSchemaVersion_StopsValidation(string version)
    {
        var result = Load($$"""{ "schemaVersion": "{{version}}", "id": "bad id", "name": "", "version": "1", "root": {} }""");
        AssertSingleError(result, DiagnosticCodes.UnsupportedSchemaVersion, "$.schemaVersion");
    }

    [Fact]
    public void MalformedSchemaVersion_IsReported() =>
        AssertSingleError(Load("""{ "schemaVersion": "v1", "id": "w", "name": "W", "version": "1", "root": {} }"""), DiagnosticCodes.InvalidSchemaVersion, "$.schemaVersion");

    [Fact]
    public void UnknownFields_AreWarnings_AndDoNotBlock()
    {
        var result = Load("""
            { "schemaVersion": "1.0", "id": "w", "name": "W", "version": "1", "author": "me",
              "root": { "id": "r", "type": "Core.Sequence", "color": "red" } }
            """);

        Assert.True(result.IsValid);
        Assert.Equal(["$.author", "$.root.color"], result.Diagnostics.Select(d => d.Path));
        Assert.All(result.Diagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
    }

    [Fact]
    public void InvalidWorkflowIdentity_IsReported()
    {
        var result = Load("""{ "schemaVersion": "1.0", "id": "has space", "name": " ", "version": "", "root": { "id": "r", "type": "Core.Sequence" } }""");

        Assert.Equal(
            [DiagnosticCodes.InvalidWorkflowId, DiagnosticCodes.InvalidWorkflowText, DiagnosticCodes.InvalidWorkflowText],
            result.Errors.Select(e => e.Code));
    }

    [Fact]
    public void DuplicateNodeIds_AreReportedWithPathAndNodeId()
    {
        var result = Load(Minimal("""
            { "id": "r", "type": "Core.Sequence", "children": [
              { "id": "a", "type": "Core.Sequence" },
              { "id": "a", "type": "Core.Sequence" } ] }
            """));

        var error = Assert.Single(result.Errors);
        Assert.Equal(DiagnosticCodes.DuplicateNodeId, error.Code);
        Assert.Equal("$.root.children[1].id", error.Path);
        Assert.Equal("a", error.NodeId);
    }

    [Fact]
    public void UnknownAndMalformedActivityTypes_AreReported()
    {
        var result = Load(Minimal("""
            { "id": "r", "type": "Core.Sequence", "children": [
              { "id": "a", "type": "Core.Browse" },
              { "id": "b", "type": "NoNamespace" } ] }
            """));

        Assert.Equal([DiagnosticCodes.UnknownActivityType, DiagnosticCodes.InvalidActivityType], result.Errors.Select(e => e.Code));
    }

    [Fact]
    public void MissingAndUnknownProperties_AreReported()
    {
        var result = Load(Minimal("""{ "id": "log", "type": "Core.Log", "properties": { "mesage": "'x'" } }"""));

        Assert.Equal([DiagnosticCodes.UnknownProperty, DiagnosticCodes.MissingProperty], result.Errors.Select(e => e.Code));
        Assert.Contains("known: message, level", result.Errors.First().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PropertyValueShapes_AreChecked()
    {
        var result = Load(Minimal("""
            { "id": "r", "type": "Core.Sequence", "children": [
              { "id": "a", "type": "Core.Log", "properties": { "message": [1], "level": "Loud" } },
              { "id": "b", "type": "Core.Assign", "properties": { "to": 5, "value": "1 +" } },
              { "id": "c", "type": "Core.InvokeWorkflow", "properties": { "workflow": 1, "arguments": "x" } } ] }
            """));

        Assert.Equal(
            [
                DiagnosticCodes.InvalidPropertyValue, DiagnosticCodes.ValueNotAllowed,
                DiagnosticCodes.InvalidPropertyValue, DiagnosticCodes.InvalidExpression,
                DiagnosticCodes.InvalidPropertyValue, DiagnosticCodes.InvalidPropertyValue,
            ],
            result.Errors.Select(e => e.Code));
    }

    [Fact]
    public void InvalidReferences_AreReported()
    {
        var result = Load(Minimal(
            """
            { "id": "r", "type": "Core.Sequence", "children": [
              { "id": "a", "type": "Core.Log", "properties": { "message": "unknownName + 1" } },
              { "id": "b", "type": "Core.Assign", "properties": { "to": "input", "value": "1" } },
              { "id": "c", "type": "Core.Assign", "properties": { "to": "nothing", "value": "1" } },
              { "id": "d", "type": "Core.Log", "properties": { "message": "len()" } },
              { "id": "e", "type": "Core.Log", "properties": { "message": "shout('x')" } } ] }
            """,
            arguments: """[ { "name": "input", "direction": "In", "type": "String" } ]"""));

        Assert.Equal(
            [DiagnosticCodes.UnknownName, DiagnosticCodes.InvalidAssignmentTarget, DiagnosticCodes.InvalidAssignmentTarget, DiagnosticCodes.InvalidExpression, DiagnosticCodes.InvalidExpression],
            result.Errors.Select(e => e.Code));
        Assert.Contains("read-only", result.Errors.ElementAt(1).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalNames_AreVisibleOnlyInTheirSlot_AndCannotShadow()
    {
        var result = Load(Minimal(
            """
            { "id": "r", "type": "Core.Sequence", "children": [
              { "id": "loop", "type": "Core.ForEach", "properties": { "items": "[1]", "itemVariable": "item" },
                "slots": { "body": { "id": "inside", "type": "Core.Log", "properties": { "message": "item" } } } },
              { "id": "outside", "type": "Core.Log", "properties": { "message": "item" } },
              { "id": "shadow", "type": "Core.ForEach", "properties": { "items": "[1]", "itemVariable": "total" },
                "slots": { "body": { "id": "noop", "type": "Core.Sequence" } } },
              { "id": "assign-local", "type": "Core.ForEach", "properties": { "items": "[1]", "itemVariable": "x" },
                "slots": { "body": { "id": "set-x", "type": "Core.Assign", "properties": { "to": "x", "value": "1" } } } } ] }
            """,
            variables: """[ { "name": "total", "type": "Int" } ]"""));

        Assert.Equal(
            [DiagnosticCodes.UnknownName, DiagnosticCodes.ShadowedName, DiagnosticCodes.InvalidAssignmentTarget],
            result.Errors.Select(e => e.Code));
        Assert.Equal("outside", result.Errors.First().NodeId);
    }

    [Fact]
    public void ChildRelationships_AreChecked()
    {
        var result = Load(Minimal("""
            { "id": "r", "type": "Core.Sequence", "children": [
              { "id": "if", "type": "Core.If", "properties": { "condition": "true" },
                "children": [ { "id": "c1", "type": "Core.Sequence" } ],
                "slots": { "otherwise": { "id": "c2", "type": "Core.Sequence" } } },
              { "id": "sw", "type": "Core.Switch", "properties": { "expression": "1" },
                "slots": { "case:1": { "id": "one", "type": "Core.Sequence" }, "default": { "id": "d", "type": "Core.Sequence" } } } ] }
            """));

        Assert.Equal(
            [DiagnosticCodes.ChildrenNotAllowed, DiagnosticCodes.UnknownSlot, DiagnosticCodes.MissingSlot],
            result.Errors.Select(e => e.Code));
    }

    [Fact]
    public void ArgumentAndVariableDefinitions_AreChecked()
    {
        var result = Load(Minimal(
            """{ "id": "r", "type": "Core.Sequence" }""",
            arguments: """
                [
                  { "name": "1bad", "direction": "In", "type": "String" },
                  { "name": "a", "direction": "Sideways", "type": "String" },
                  { "name": "b", "direction": "In", "type": "Text" },
                  { "name": "c", "direction": "In", "type": "Int", "default": "zero" },
                  { "name": "d", "direction": "Out", "type": "Int", "required": true },
                  { "name": "e", "direction": "In", "type": "Int" }
                ]
                """,
            variables: """[ { "name": "e", "type": "Int" }, { "name": "f", "type": "List", "default": {} } ]"""));

        Assert.Equal(
            [
                DiagnosticCodes.InvalidName, DiagnosticCodes.InvalidDirection, DiagnosticCodes.InvalidDataType,
                DiagnosticCodes.InvalidDefault, DiagnosticCodes.OutArgumentMisuse, DiagnosticCodes.DuplicateName,
                DiagnosticCodes.InvalidDefault,
            ],
            result.Errors.Select(e => e.Code));
    }

    [Fact]
    public void MultipleErrors_AreReportedInOnePass()
    {
        var result = Load("""
            { "schemaVersion": "1.0", "id": "broken example", "name": "W", "version": "1",
              "arguments": [ { "name": "input", "direction": "In", "type": "Text" } ],
              "root": { "id": "main", "type": "Core.Sequence", "children": [
                { "id": "a", "type": "Core.Assign", "properties": { "to": "missing", "value": "1 +" } },
                { "id": "a", "type": "Core.Browse" },
                { "id": "c", "type": "Core.Log", "properties": { "mesage": "'typo'" } } ] } }
            """);

        Assert.False(result.IsValid);
        Assert.Null(result.Workflow);
        Assert.Equal(8, result.Errors.Count());
        Assert.Equal(8, result.Errors.Select(e => e.Code).Count());
    }

    [Fact]
    public void Diagnostics_FormatLikeCompilerMessages()
    {
        var diagnostic = new ValidationDiagnostic(DiagnosticCodes.DuplicateNodeId, DiagnosticSeverity.Error, "Duplicate.", "$.root.id", "a");
        Assert.Equal("error MYRPA1031 $.root.id: Duplicate.", diagnostic.ToString());
    }

    [Fact]
    public void Loader_AcceptsCommentsAndTrailingCommas()
    {
        var result = Load("""
            // hand-written workflow
            { "schemaVersion": "1.0", "id": "w", "name": "W", "version": "1",
              "root": { "id": "r", "type": "Core.Sequence", }, }
            """);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void DefaultsAreTypedByDeclaration()
    {
        var result = Load(Minimal(
            """{ "id": "r", "type": "Core.Sequence" }""",
            variables: """[ { "name": "price", "type": "Decimal", "default": 10 }, { "name": "when", "type": "DateTime", "default": "2026-01-01" } ]"""));

        Assert.True(result.IsValid);
        Assert.Equal(10m, result.Workflow.Variables[0].DefaultValue);
        Assert.IsType<DateTimeOffset>(result.Workflow.Variables[1].DefaultValue);
        Assert.Equal(WorkflowDataType.DateTime, result.Workflow.Variables[1].Type);
    }
}
