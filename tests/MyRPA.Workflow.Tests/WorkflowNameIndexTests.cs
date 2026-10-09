using MyRPA.Workflow.Expressions;
using MyRPA.Workflow.Validation;
using MyRPA.Workflow.Values;

namespace MyRPA.Workflow.Tests;

/// <summary>The name index (ADR-0041): what validation sees in scope at each node, and exact, scope-aware references.</summary>
public sealed class WorkflowNameIndexTests
{
    private const string Declarations = """
        "arguments": [ { "name": "who", "direction": "In", "type": "String" }, { "name": "total", "direction": "Out", "type": "Int" } ],
        "variables": [ { "name": "items", "type": "List" }, { "name": "n", "type": "Int", "default": 0 } ]
        """;

    private static readonly WorkflowLoader _loader = new(new TestCatalog());

    // children[0] Assign, [1] ForEach with a local 'item', [2] TryCatch with a local 'err'.
    private static readonly string _workflow = $$"""
        { "schemaVersion": "1.0", "id": "w", "name": "W", "version": "1", {{Declarations}},
          "root": { "id": "main", "type": "Core.Sequence", "children": [
            { "id": "set", "type": "Core.Assign", "properties": { "to": "total", "value": "n + len(items)" } },
            { "id": "loop", "type": "Core.ForEach", "properties": { "items": "items", "itemVariable": "item" },
              "slots": { "body": { "id": "say", "type": "Core.Log", "properties": { "message": "'item ' + item" } } } },
            { "id": "guard", "type": "Core.TryCatch", "properties": { "exceptionVariable": "err" },
              "slots": { "try": { "id": "t", "type": "Core.Log", "properties": { "message": "who" } },
                         "catch": { "id": "c", "type": "Core.Log", "properties": { "message": "err.message" } } } } ] } }
        """;

    private static string[] Names(IEnumerable<WorkflowSymbol> symbols) => [.. symbols.Select(s => s.Name)];

    [Fact]
    public void InScope_FollowsTheLoadersScopes_LocalsOnlyInTheirSlots()
    {
        var index = _loader.IndexNames(_workflow);

        Assert.Equal(["who", "total", "items", "n"], Names(index.InScope("$.arguments[0].default")));
        Assert.Equal(["who", "total", "items", "n"], Names(index.InScope("$.root.children[1].properties.items")));
        Assert.Equal(["who", "total", "items", "n", "item"], Names(index.InScope("$.root.children[1].slots.body.properties.message")));
        Assert.Equal(["who", "total", "items", "n", "err"], Names(index.InScope("$.root.children[2].slots.catch")));
        Assert.Equal(["who", "total", "items", "n"], Names(index.InScope("$.root.children[2].slots.try.properties.message")));

        var who = index.Symbols[0];
        Assert.Equal((WorkflowSymbolKind.Argument, WorkflowDataType.String, (ArgumentDirection?)ArgumentDirection.In, "$.arguments[0]"), (who.Kind, who.Type, who.Direction, who.DeclarationPath));
        var item = index.Resolve("$.root.children[1].slots.body", "item")!;
        Assert.Equal((WorkflowSymbolKind.Local, WorkflowDataType.Object, (ArgumentDirection?)null, "$.root.children[1].properties.itemVariable"), (item.Kind, item.Type, item.Direction, item.DeclarationPath));
    }

    [Fact]
    public void ReferencesTo_AreExact_DeclarationAssignmentAndExpressions_NeverMembersOrStrings()
    {
        var index = _loader.IndexNames(_workflow);

        Assert.Equal(
            [new("$.variables[1].name", 0, 1, true), new("$.root.children[0].properties.value", 0, 1, false)],
            index.ReferencesTo(index.Resolve("$.root.children[0].properties.value", "n")!));
        Assert.Equal(
            [new("$.arguments[1].name", 0, 5, true), new("$.root.children[0].properties.to", 0, 5, false)],
            index.ReferencesTo(index.Resolve("$.root", "total")!));
        // "'item ' + item": the text inside the string is not a reference; the name is at offset 10.
        Assert.Equal(
            [new("$.root.children[1].properties.itemVariable", 0, 4, true), new("$.root.children[1].slots.body.properties.message", 10, 4, false)],
            index.ReferencesTo(index.Resolve("$.root.children[1].slots.body.properties.message", "item")!));
        // "err.message": 'message' is a member, not a name.
        Assert.Equal(
            [new("$.root.children[2].properties.exceptionVariable", 0, 3, true), new("$.root.children[2].slots.catch.properties.message", 0, 3, false)],
            index.ReferencesTo(index.Resolve("$.root.children[2].slots.catch", "err")!));
    }

    [Fact]
    public void SameNamedLocals_InTwoLoops_AreSeparateDeclarations()
    {
        var index = _loader.IndexNames($$"""
            { "schemaVersion": "1.0", "id": "w", "name": "W", "version": "1", {{Declarations}},
              "root": { "id": "main", "type": "Core.Sequence", "children": [
                { "id": "a", "type": "Core.ForEach", "properties": { "items": "items", "itemVariable": "item" }, "slots": { "body": { "id": "la", "type": "Core.Log", "properties": { "message": "item" } } } },
                { "id": "b", "type": "Core.ForEach", "properties": { "items": "items", "itemVariable": "item" }, "slots": { "body": { "id": "lb", "type": "Core.Log", "properties": { "message": "item" } } } } ] } }
            """);

        var first = index.Resolve("$.root.children[0].slots.body", "item")!;
        var second = index.Resolve("$.root.children[1].slots.body", "item")!;

        Assert.NotSame(first, second);
        Assert.Equal(["$.root.children[0].properties.itemVariable", "$.root.children[0].slots.body.properties.message"], index.ReferencesTo(first).Select(r => r.Path));
        Assert.Equal(["$.root.children[1].properties.itemVariable", "$.root.children[1].slots.body.properties.message"], index.ReferencesTo(second).Select(r => r.Path));
        Assert.Null(index.Resolve("$.root", "item"));
    }

    [Fact]
    public void DocumentsWithErrors_AreIndexedAsFarAsTheyCanBeRead()
    {
        var index = _loader.IndexNames($$"""
            { "schemaVersion": "1.0", "id": "w", "name": "W", "version": "1", {{Declarations}},
              "root": { "id": "main", "type": "Core.Sequence", "children": [
                { "id": "broken", "type": "Core.Log", "properties": { "message": "n +" } },
                { "id": "unknown", "type": "Vendor.Missing" },
                { "id": "ok", "type": "Core.Log", "properties": { "message": "n * 2" } } ] } }
            """);

        Assert.Equal(["$.variables[1].name", "$.root.children[2].properties.message"], index.ReferencesTo(index.Resolve("$.root", "n")!).Select(r => r.Path));
        Assert.Empty(_loader.IndexNames("not json").Symbols);
        Assert.Empty(_loader.IndexNames("""{ "schemaVersion": "9.0" }""").InScope("$.root"));
    }

    [Fact]
    public void IndexingNames_DoesNotChangeTheDiagnostics_AndKeepsTheKnownNamesOfAnExpressionWithAnUnknownOne()
    {
        var withUnknown = _workflow.Replace("'item ' + item", "missing + item", StringComparison.Ordinal);
        var index = _loader.IndexNames(withUnknown);

        Assert.Equal(["MYRPA1044"], _loader.Load(withUnknown).Diagnostics.Select(d => d.Code));
        Assert.Equal(
            ["$.root.children[1].slots.body.properties.message"],
            index.ReferencesTo(index.Symbols.Single(s => s.Name == "item")).Where(r => !r.IsDeclaration).Select(r => r.Path));
    }

    [Fact]
    public void NameReferences_ListEveryNameWithItsPosition_NotFunctionsMembersOrStrings()
    {
        var expression = WorkflowExpression.Parse("upper(a) + x.a + 'a' + b");

        Assert.Equal([new("a", 6, 1), new("x", 11, 1), new("b", 23, 1)], expression.NameReferences);
    }

    [Fact]
    public void EveryFunction_HasASignatureAndADescription()
    {
        Assert.Equal(ExpressionFunctions.Names.Order(StringComparer.Ordinal), ExpressionFunctions.Functions.Select(f => f.Name));
        Assert.All(ExpressionFunctions.Functions, f =>
        {
            Assert.StartsWith(f.Name + "(", f.Signature, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(f.Description), f.Name);
            Assert.True(ExpressionFunctions.TryGetArity(f.Name, out var min, out var max) && min == f.MinArguments && max == f.MaxArguments);
        });
    }
}
