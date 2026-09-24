using System.Text.Json;
using MyRPA.Workflow.Values;

namespace MyRPA.Workflow.Tests;

public sealed class WorkflowValuesTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Theory]
    [InlineData("\"text\"", WorkflowDataType.String, "text")]
    [InlineData("42", WorkflowDataType.Int, 42L)]
    [InlineData("true", WorkflowDataType.Boolean, true)]
    [InlineData("null", WorkflowDataType.Int, null)]
    public void FromJson_MatchingTypes(string json, WorkflowDataType type, object? expected)
    {
        Assert.True(WorkflowValues.TryFromJson(Json(json), type, out var value, out _));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void FromJson_DecimalDateTimeListDictionaryObject()
    {
        Assert.True(WorkflowValues.TryFromJson(Json("1.25"), WorkflowDataType.Decimal, out var d, out _));
        Assert.Equal(1.25m, d);

        Assert.True(WorkflowValues.TryFromJson(Json("\"2026-09-23T10:00:00+07:00\""), WorkflowDataType.DateTime, out var dt, out _));
        Assert.Equal(new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.FromHours(7)), dt);

        Assert.True(WorkflowValues.TryFromJson(Json("[1, 2.5, \"x\", {\"k\": null}]"), WorkflowDataType.List, out var list, out _));
        var items = Assert.IsAssignableFrom<IReadOnlyList<object?>>(list);
        Assert.Equal(1L, items[0]);
        Assert.Equal(2.5m, items[1]);
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(items[3]);

        Assert.True(WorkflowValues.TryFromJson(Json("{\"a\": [1]}"), WorkflowDataType.Dictionary, out var map, out _));
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(map);

        Assert.True(WorkflowValues.TryFromJson(Json("7"), WorkflowDataType.Object, out var obj, out _));
        Assert.Equal(7L, obj);
    }

    [Theory]
    [InlineData("\"42\"", WorkflowDataType.Int)]
    [InlineData("1.5", WorkflowDataType.Int)]
    [InlineData("1", WorkflowDataType.String)]
    [InlineData("\"yesterday\"", WorkflowDataType.DateTime)]
    [InlineData("{}", WorkflowDataType.List)]
    [InlineData("[]", WorkflowDataType.Dictionary)]
    [InlineData("1e400", WorkflowDataType.Object)]
    public void FromJson_MismatchedTypes_Fail(string json, WorkflowDataType type)
    {
        Assert.False(WorkflowValues.TryFromJson(Json(json), type, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Convert_OnlyIntWidensToDecimal()
    {
        Assert.True(WorkflowValues.TryConvert(5L, WorkflowDataType.Decimal, out var widened, out _));
        Assert.Equal(5m, widened);
        Assert.False(WorkflowValues.TryConvert(5m, WorkflowDataType.Int, out _, out _));
        Assert.False(WorkflowValues.TryConvert("5", WorkflowDataType.Int, out _, out _));
        Assert.True(WorkflowValues.TryConvert(null, WorkflowDataType.Int, out _, out _));
        Assert.True(WorkflowValues.TryConvert("anything", WorkflowDataType.Object, out _, out _));
    }

    [Fact]
    public void Normalize_AcceptsPrimitivesAndCollections_RejectsArbitraryObjects()
    {
        Assert.True(WorkflowValues.TryNormalize(3, out var i, out _));
        Assert.Equal(3L, i);
        Assert.True(WorkflowValues.TryNormalize(new List<object?> { 1, "a" }, out var list, out _));
        Assert.Equal([1L, "a"], Assert.IsAssignableFrom<IReadOnlyList<object?>>(list));
        Assert.True(WorkflowValues.TryNormalize(new Dictionary<string, object?> { ["k"] = 2.5 }, out var map, out _));
        Assert.Equal(2.5m, Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(map)["k"]);

        Assert.False(WorkflowValues.TryNormalize(new Uri("https://example.org"), out _, out var error));
        Assert.Contains("cannot be used", error, StringComparison.Ordinal);
        Assert.False(WorkflowValues.TryNormalize(new Dictionary<int, object?> { [1] = 1 }, out _, out _));
    }

    [Theory]
    [InlineData("hello", WorkflowDataType.String, "hello")]
    [InlineData("-12", WorkflowDataType.Int, -12L)]
    [InlineData("false", WorkflowDataType.Boolean, false)]
    public void ParseText_ForCommandLineArguments(string text, WorkflowDataType type, object expected)
    {
        Assert.True(WorkflowValues.TryParseText(text, type, out var value, out _));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void ParseText_JsonForCollections_AndErrors()
    {
        Assert.True(WorkflowValues.TryParseText("[1,2]", WorkflowDataType.List, out var list, out _));
        Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<object?>>(list).Count);
        Assert.True(WorkflowValues.TryParseText("1.5", WorkflowDataType.Decimal, out var d, out _));
        Assert.Equal(1.5m, d);
        Assert.False(WorkflowValues.TryParseText("abc", WorkflowDataType.Int, out _, out _));
        Assert.False(WorkflowValues.TryParseText("[1,", WorkflowDataType.List, out _, out var error));
        Assert.Contains("Invalid JSON", error, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonAndDisplay_AreInvariantAndReadable()
    {
        var value = WorkflowValues.Dictionary([new("expr", "a + b"), new("n", 1.5m), new("when", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))]);

        Assert.Equal("{\"expr\":\"a + b\",\"n\":1.5,\"when\":\"2026-01-01T00:00:00.0000000+00:00\"}", WorkflowValues.ToJsonString(value));
        Assert.Equal("1.5", WorkflowValues.ToDisplayString(1.5m));
        Assert.Equal("true", WorkflowValues.ToDisplayString(true));
        Assert.Equal(string.Empty, WorkflowValues.ToDisplayString(null));
    }

    [Fact]
    public void DataTypeNames_AreCaseSensitiveAndNotNumeric()
    {
        Assert.True(WorkflowValues.TryParseDataType("Dictionary", out var type));
        Assert.Equal(WorkflowDataType.Dictionary, type);
        Assert.False(WorkflowValues.TryParseDataType("string", out _));
        Assert.False(WorkflowValues.TryParseDataType("1", out _));
    }
}
