namespace MyRPA.Workflow.Tests;

public sealed class WorkflowSchemaVersionTests
{
    [Fact]
    public void Current_IsOnePointZero() => Assert.Equal("1.0", WorkflowSchemaVersion.Current.ToString());

    [Theory]
    [InlineData("1.0", 1, 0)]
    [InlineData("2.15", 2, 15)]
    public void TryParse_Valid(string text, int major, int minor)
    {
        Assert.True(WorkflowSchemaVersion.TryParse(text, out var version));
        Assert.Equal(new WorkflowSchemaVersion(major, minor), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.0.0")]
    [InlineData("0.9")]
    [InlineData("-1.0")]
    [InlineData("a.b")]
    public void TryParse_Invalid(string? text) => Assert.False(WorkflowSchemaVersion.TryParse(text, out _));

    [Fact]
    public void Comparison_OrdersByMajorThenMinor()
    {
        var v10 = new WorkflowSchemaVersion(1, 0);
        var v19 = new WorkflowSchemaVersion(1, 9);
        var v20 = new WorkflowSchemaVersion(2, 0);

        Assert.True(v10 < v19);
        Assert.True(v19 < v20);
        Assert.True(v20 > v10);
        Assert.True(v10 <= new WorkflowSchemaVersion(1, 0));
        Assert.True(v20 >= v19);
    }

    [Fact]
    public void Constructor_RejectsOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkflowSchemaVersion(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkflowSchemaVersion(1, -1));
    }
}
