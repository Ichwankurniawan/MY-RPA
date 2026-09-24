using MyRPA.Sdk.Automation;
using MyRPA.Sdk.Plugins;

namespace MyRPA.Sdk.Tests;

/// <summary>Versions, identities and selector data types of the SDK.</summary>
public sealed class SdkTypeTests
{
    [Fact]
    public void SdkVersion_IsOnePointZero() => Assert.Equal("1.0", AutomationSdk.Version.ToString());

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.1", false)]
    [InlineData("0.9", false)]
    [InlineData("2.0", false)]
    public void SdkVersion_SupportsSameMajorAndLowerOrEqualMinor(string required, bool supported)
    {
        Assert.True(SdkVersion.TryParse(required, out var version));
        Assert.Equal(supported, AutomationSdk.Version.Supports(version));
        Assert.True(new SdkVersion(1, 3).Supports(new SdkVersion(1, 1)));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.0.0")]
    [InlineData("01.0")]
    [InlineData("a.b")]
    [InlineData("")]
    public void SdkVersion_RejectsBadText(string text) => Assert.False(SdkVersion.TryParse(text, out _));

    [Theory]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.2.3", "1.10.0", -1)]
    [InlineData("2.0.0-beta.1", "2.0.0", -1)]
    [InlineData("2.0.0-beta.2", "2.0.0-beta.1", 1)]
    public void PluginVersion_Compares(string left, string right, int sign) =>
        Assert.Equal(sign, Math.Sign(PluginVersion.Parse(left).CompareTo(PluginVersion.Parse(right))));

    [Theory]
    [InlineData("1.4.2", "1.2.0", true)]
    [InlineData("1.2.0", "1.2.0", true)]
    [InlineData("1.1.9", "1.2.0", false)]
    [InlineData("2.0.0", "1.2.0", false)]
    public void PluginVersion_DependencyUsesCaretSemantics(string actual, string minimum, bool compatible) =>
        Assert.Equal(compatible, PluginVersion.Parse(actual).IsCompatibleWith(PluginVersion.Parse(minimum)));

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.0.0+build")]
    [InlineData("1.0.0-")]
    [InlineData("v1.0.0")]
    public void PluginVersion_RejectsBadText(string text) => Assert.False(PluginVersion.TryParse(text, out _));

    [Fact]
    public void PluginId_IsCaseInsensitive()
    {
        Assert.Equal(new PluginId("Contoso.Browser"), new PluginId("contoso.BROWSER"));
        Assert.False(PluginId.IsValid("Browser"));
        Assert.False(PluginId.IsValid("Contoso.Browser-Plugin"));
    }

    [Fact]
    public void ProviderId_UsesTheActivityNameFormat()
    {
        Assert.Equal("Browser", new AutomationProviderId("Browser.Playwright").Namespace);
        Assert.Throws<ArgumentException>(() => new AutomationProviderId("Playwright"));
    }

    [Fact]
    public void Selector_IsProviderAndOrderedSteps()
    {
        var selector = new Selector(new AutomationProviderId("Demo.Text"), [new SelectorStep(SelectorStrategies.Role, "button"), new SelectorStep("Demo.Field", "ok")]);

        Assert.Equal("Demo.Text: Role=button > Demo.Field=ok", selector.ToString());
        Assert.Throws<ArgumentException>(() => new Selector(selector.Provider, []));
        Assert.Throws<ArgumentException>(() => new SelectorStep("not a strategy", "x"));
    }

    [Fact]
    public void SelectorMatch_RequireSingle_ClassifiesMissingAndAmbiguousMatches()
    {
        var selector = new Selector(new AutomationProviderId("Demo.Text"), [new SelectorStep(SelectorStrategies.Text, "x")]);
        var element = new FakeElement();

        Assert.Same(element, new SelectorMatch(selector, [element]).RequireSingle());
        Assert.Equal(AutomationErrorTypes.ElementNotFound, Assert.Throws<AutomationException>(() => new SelectorMatch(selector, []).RequireSingle()).ErrorType);
        Assert.Equal(AutomationErrorTypes.AmbiguousMatch, Assert.Throws<AutomationException>(() => new SelectorMatch(selector, [element, element]).RequireSingle()).ErrorType);
    }

    private sealed class FakeElement : IAutomationElement
    {
        public AutomationProviderId Provider { get; } = new("Demo.Text");

        public string Description => "fake";

        public ValueTask ClickAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask TypeTextAsync(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<string> GetTextAsync(CancellationToken cancellationToken) => ValueTask.FromResult("x");

        public ValueTask<string?> GetAttributeAsync(string name, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
    }
}
