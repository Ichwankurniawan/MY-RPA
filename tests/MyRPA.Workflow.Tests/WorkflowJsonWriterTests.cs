using MyRPA.Workflow.Serialization;
using MyRPA.Workflow.Validation;

namespace MyRPA.Workflow.Tests;

public sealed class WorkflowJsonWriterTests
{
    private readonly WorkflowLoader _loader = new(new TestCatalog());

    private const string Source = """
        {
          "schemaVersion": "1.0",
          "id": "round-trip",
          "name": "Round trip",
          "version": "3.0.0",
          "arguments": [
            { "name": "count", "direction": "In", "type": "Int", "required": true },
            { "name": "result", "direction": "Out", "type": "String" }
          ],
          "variables": [ { "name": "tags", "type": "List", "default": ["a", 1] } ],
          "root": {
            "id": "main", "type": "Core.Sequence",
            "children": [
              { "id": "set", "type": "Core.Assign", "properties": { "to": "result", "value": "'n=' + count" } },
              { "id": "sw", "type": "Core.Switch", "properties": { "expression": 1 },
                "slots": { "case:1": { "id": "one", "type": "Core.Log", "properties": { "message": true, "level": "Warning" } } } },
              { "id": "call", "type": "Core.InvokeWorkflow",
                "properties": { "workflow": "child.json", "arguments": { "x": "count", "y": 2.5 }, "outputs": { "out": "result" } } }
            ]
          }
        }
        """;

    [Fact]
    public void LoadWriteLoad_ProducesEquivalentWorkflow()
    {
        var first = _loader.Load(Source);
        Assert.True(first.IsValid, string.Join(Environment.NewLine, first.Diagnostics));

        var written = WorkflowJsonWriter.Write(first.Workflow);
        var second = _loader.Load(written);

        Assert.True(second.IsValid, string.Join(Environment.NewLine, second.Diagnostics));
        Assert.Equal(written, WorkflowJsonWriter.Write(second.Workflow));
        Assert.Equal(
            first.Workflow.Root.DescendantsAndSelf().Select(n => n.Id.Value),
            second.Workflow.Root.DescendantsAndSelf().Select(n => n.Id.Value));
    }

    [Fact]
    public void Write_KeepsExpressionsReadable_AndLiteralsAsJson()
    {
        var written = WorkflowJsonWriter.Write(_loader.Load(Source).Workflow!);

        Assert.Contains("\"value\": \"'n=' + count\"", written, StringComparison.Ordinal);
        Assert.Contains("\"expression\": 1", written, StringComparison.Ordinal);
        Assert.Contains("\"message\": true", written, StringComparison.Ordinal);
        Assert.Contains("\"y\": 2.5", written, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": \"1.0\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", written, StringComparison.Ordinal);
    }
}
