using Microsoft.Extensions.DependencyInjection;
using MyRPA.Core.Activities;

namespace MyRPA.Activities.Tests;

public sealed class ActivityCatalogTests
{
    private static ActivityDescriptor Descriptor(string name) =>
        new(new ActivityTypeName(name), name, "Test");

    [Fact]
    public void Catalog_ListsDescriptorsSortedByName()
    {
        var catalog = new ActivityCatalog([Descriptor("Test.B"), Descriptor("Test.A")]);

        Assert.Equal(["Test.A", "Test.B"], catalog.Descriptors.Select(d => d.TypeName.Value));
    }

    [Fact]
    public void TryGet_RegisteredAndUnknownNames()
    {
        var catalog = new ActivityCatalog([Descriptor("Test.A")]);

        Assert.True(catalog.TryGet(new ActivityTypeName("Test.A"), out var found));
        Assert.Equal("Test.A", found.DisplayName);
        Assert.False(catalog.TryGet(new ActivityTypeName("Test.Missing"), out _));
    }

    [Fact]
    public void Catalog_DuplicateNames_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ActivityCatalog([Descriptor("Test.A"), Descriptor("Test.A")]));
        Assert.Contains("Test.A", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddMyRpaActivities_WithoutRegistrations_IsEmpty()
    {
        // Phase 1 ships no built-in activities; nothing is discovered implicitly (ADR-0005).
        using var provider = new ServiceCollection().AddMyRpaActivities().BuildServiceProvider(validateScopes: true);

        Assert.Empty(provider.GetRequiredService<IActivityCatalog>().Descriptors);
    }

    [Fact]
    public void AddActivityDescriptor_RegistersExplicitly()
    {
        using var provider = new ServiceCollection()
            .AddMyRpaActivities()
            .AddActivityDescriptor(Descriptor("Test.A"))
            .AddActivityDescriptor(Descriptor("Test.B"))
            .BuildServiceProvider(validateScopes: true);

        var catalog = provider.GetRequiredService<IActivityCatalog>();
        Assert.Equal(2, catalog.Descriptors.Count);
        Assert.True(catalog.TryGet(new ActivityTypeName("Test.B"), out _));
    }
}
