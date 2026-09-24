using MyRPA.Core.Activities;

namespace MyRPA.Core.Tests;

public sealed class ActivityMetadataTests
{
    [Theory]
    [InlineData("Core.Log")]
    [InlineData("Browser.Click")]
    [InlineData("Acme.Invoices.Extract2")]
    public void ActivityTypeName_ValidNames_Accepted(string value) =>
        Assert.Equal(value, new ActivityTypeName(value).Value);

    [Theory]
    [InlineData("Log")] // namespace required to avoid collisions between plugins
    [InlineData("Core.")]
    [InlineData(".Log")]
    [InlineData("Core..Log")]
    [InlineData("1Core.Log")]
    [InlineData("Core.Log-2")]
    [InlineData("System.Type, mscorlib")] // CLR type names are never activity names (ADR-0008)
    [InlineData("")]
    public void ActivityTypeName_InvalidNames_Rejected(string value)
    {
        Assert.False(ActivityTypeName.IsValid(value));
        Assert.Throws<ArgumentException>(() => new ActivityTypeName(value));
    }

    [Fact]
    public void ActivityTypeName_Namespace_IsFirstSegment() =>
        Assert.Equal("Acme", new ActivityTypeName("Acme.Invoices.Extract").Namespace);

    [Fact]
    public void ActivityTypeName_EqualityIsOrdinalByValue()
    {
        Assert.Equal(new ActivityTypeName("Core.Log"), new ActivityTypeName("Core.Log"));
        Assert.NotEqual(new ActivityTypeName("Core.Log"), new ActivityTypeName("core.log"));
    }

    [Fact]
    public void ActivityDescriptor_RequiresDisplayNameAndCategory()
    {
        var name = new ActivityTypeName("Core.Log");
        Assert.Throws<ArgumentException>(() => new ActivityDescriptor(name, " ", "Diagnostics"));
        Assert.Throws<ArgumentException>(() => new ActivityDescriptor(name, "Log", ""));

        var descriptor = new ActivityDescriptor(name, "Log", "Diagnostics", "Writes a message.");
        Assert.Equal("Writes a message.", descriptor.Description);
    }

    [Fact]
    public void ActivityDescriptor_DeclaresPropertiesSlotsAndChildren()
    {
        var descriptor = new ActivityDescriptor(
            new ActivityTypeName("Test.Loop"),
            "Loop",
            "Test",
            properties:
            [
                new("items", ActivityPropertyKind.Expression, isRequired: true),
                new("item", ActivityPropertyKind.LocalName, isRequired: true, scopeSlots: ["body"]),
            ],
            slots: [new("body", isRequired: true), new("case:", isPrefix: true)]);

        Assert.False(descriptor.AllowsChildren);
        Assert.Equal(ActivityPropertyKind.LocalName, descriptor.FindProperty("item")!.Kind);
        Assert.Null(descriptor.FindProperty("missing"));
        Assert.Equal("body", descriptor.FindSlot("body")!.Name);
        Assert.Equal("case:", descriptor.FindSlot("case:gold")!.Name);
        Assert.Null(descriptor.FindSlot("case:"));
        Assert.Null(descriptor.FindSlot("else"));
    }

    [Fact]
    public void ActivityDescriptor_RejectsInconsistentSchemas()
    {
        var name = new ActivityTypeName("Test.Bad");
        Assert.Throws<ArgumentException>(() => new ActivityDescriptor(name, "Bad", "Test",
            properties: [new("x", ActivityPropertyKind.Expression), new("x", ActivityPropertyKind.Text)]));
        Assert.Throws<ArgumentException>(() => new ActivityDescriptor(name, "Bad", "Test",
            slots: [new("body"), new("body")]));
        Assert.Throws<ArgumentException>(() => new ActivityDescriptor(name, "Bad", "Test",
            properties: [new("item", ActivityPropertyKind.LocalName, scopeSlots: ["body"])]));
        Assert.Throws<ArgumentException>(() => new ActivityPropertyDefinition("x", ActivityPropertyKind.Expression, scopeSlots: ["body"]));
        Assert.Throws<ArgumentException>(() => new ActivitySlotDefinition("case:", isRequired: true, isPrefix: true));
    }
}
