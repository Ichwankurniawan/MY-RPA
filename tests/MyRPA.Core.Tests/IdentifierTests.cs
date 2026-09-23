using MyRPA.Core.Identifiers;

namespace MyRPA.Core.Tests;

public sealed class IdentifierTests
{
    [Theory]
    [InlineData("invoice-processing")]
    [InlineData("n1")]
    [InlineData("Orders_2026.v2:main")]
    public void IsValid_AllowedCharacters_ReturnsTrue(string value) => Assert.True(Identifier.IsValid(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/not-allowed")]
    [InlineData("quote'")]
    [InlineData("ünicode")]
    public void IsValid_DisallowedInput_ReturnsFalse(string? value) => Assert.False(Identifier.IsValid(value));

    [Fact]
    public void IsValid_LongerThanMaxLength_ReturnsFalse()
    {
        Assert.True(Identifier.IsValid(new string('a', Identifier.MaxLength)));
        Assert.False(Identifier.IsValid(new string('a', Identifier.MaxLength + 1)));
    }

    [Fact]
    public void WorkflowId_InvalidValue_Throws() =>
        Assert.Throws<ArgumentException>(() => new WorkflowId("not valid"));

    [Fact]
    public void WorkflowId_SameValue_AreEqual()
    {
        Assert.Equal(new WorkflowId("wf-1"), new WorkflowId("wf-1"));
        Assert.Equal("wf-1", new WorkflowId("wf-1").ToString());
    }

    [Fact]
    public void NodeId_TryCreate_ReportsValidity()
    {
        Assert.True(NodeId.TryCreate("step-1", out var ok));
        Assert.Equal("step-1", ok.Value);
        Assert.False(NodeId.TryCreate("step 1", out var bad));
        Assert.Null(bad);
    }

    [Fact]
    public void CorrelationId_New_IsValidAndUnique()
    {
        var a = CorrelationId.New();
        var b = CorrelationId.New();
        Assert.True(Identifier.IsValid(a.Value));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ExecutionId_Empty_Throws() =>
        Assert.Throws<ArgumentException>(() => new ExecutionId(Guid.Empty));

    [Fact]
    public void ExecutionId_ToStringAndTryParse_RoundTrip()
    {
        var id = ExecutionId.New();
        Assert.True(ExecutionId.TryParse(id.ToString(), out var parsed));
        Assert.Equal(id, parsed);
        Assert.Equal(32, id.ToString().Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("not-a-guid")]
    public void ExecutionId_TryParse_InvalidInput_ReturnsFalse(string? value) =>
        Assert.False(ExecutionId.TryParse(value, out _));

    [Fact]
    public void ExecutionId_New_IsTimeOrdered()
    {
        var first = ExecutionId.New();
        Thread.Sleep(2);
        var second = ExecutionId.New();
        Assert.True(first.Value.CompareTo(second.Value) < 0);
    }
}
