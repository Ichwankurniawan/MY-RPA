using MyRPA.Core.Execution;
using static MyRPA.Browser.Playwright.Tests.Nodes;

namespace MyRPA.Browser.Playwright.Tests;

/// <summary>Elements: find, click, type, get text, get attribute, wait, select; missing, hidden and ambiguous elements.</summary>
[Collection(BrowserTestGroup.Name)]
public sealed class ElementTests(BrowserHost host) : IClassFixture<BrowserHost>
{
    private Task<MyRPA.Workflow.Execution.WorkflowExecutionResult> Run(string nodes, params string[] outputs) => host.RunAsync(host.Open() + ", " + nodes, outputs);

    [Fact]
    public async Task GetAttribute_ReturnsTheValue_OrNullWhenAbsent()
    {
        var result = await Run(
            Node("Browser.GetAttribute", "\"selector\": \"#link\", \"name\": \"href\", \"to\": \"href\"") + ", "
            + Node("Browser.GetAttribute", "\"selector\": \"#link\", \"name\": \"data-kind\", \"to\": \"kind\"") + ", "
            + Node("Browser.GetAttribute", "\"selector\": \"#link\", \"name\": \"title\", \"to\": \"missing\""),
            "href", "kind", "missing");

        AssertSucceeded(result);
        Assert.Equal("/other", result.Outputs["href"]);
        Assert.Equal("nav", result.Outputs["kind"]);
        Assert.Null(result.Outputs["missing"]);
    }

    [Fact]
    public async Task TypeText_ReplacesByDefault_AndAppendsWhenClearIsFalse()
    {
        var result = await Run(
            Node("Browser.TypeText", "\"selector\": \"#name\", \"text\": \"'Grace'\"") + ", "
            + Node("Browser.TypeText", "\"selector\": \"#name\", \"text\": \"'Ada'\"") + ", "
            + Node("Browser.TypeText", "\"selector\": \"#name\", \"text\": \"' Lovelace'\", \"clear\": \"false\"") + ", "
            + Node("Browser.Click", "\"selector\": \"#greet\"") + ", "
            + Node("Browser.GetText", "\"selector\": \"#out\", \"to\": \"out\""),
            "out");

        AssertSucceeded(result);
        Assert.Equal("Hello, Ada Lovelace", result.Outputs["out"]);
    }

    [Fact]
    public async Task MissingElement_FailsWithElementNotFound_AfterItsTimeout()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await Run(Node("Browser.Click", "\"selector\": \"#does-not-exist\", \"timeoutMilliseconds\": 400"));

        AssertFailed(result, ErrorTypes.ElementNotFound);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("#hidden")]
    [InlineData("#disabled")]
    public async Task ElementThatNeverBecomesActionable_FailsWithElementTimeout(string selector)
    {
        var result = await Run(Node("Browser.Click", $"\"selector\": \"{selector}\", \"timeoutMilliseconds\": 400"));

        AssertFailed(result, ErrorTypes.ElementTimeout);
    }

    [Fact]
    public async Task MoreThanOneMatch_FailsWithAmbiguousMatch()
    {
        var result = await Run(Node("Browser.GetText", "\"selector\": \".item\", \"to\": \"t\", \"timeoutMilliseconds\": 1000"), "t");

        AssertFailed(result, ErrorTypes.AmbiguousMatch);
    }

    [Fact]
    public async Task WaitForElement_WaitsForLateElements_AndForRemoval()
    {
        var result = await Run(
            Node("Browser.WaitForElement", "\"selector\": \"#appeared\"") + ", "
            + Node("Browser.WaitForElement", "\"selector\": \"#hidden\", \"state\": \"hidden\"") + ", "
            + Node("Browser.Click", "\"selector\": \"#vanish\"") + ", "
            + Node("Browser.WaitForElement", "\"selector\": \"#vanish\", \"state\": \"detached\""));

        AssertSucceeded(result);
    }

    [Theory]
    [InlineData("\"selector\": \"#never\", \"timeoutMilliseconds\": 300", ErrorTypes.ElementNotFound)]
    [InlineData("\"selector\": \"#title\", \"state\": \"hidden\", \"timeoutMilliseconds\": 300", ErrorTypes.ElementTimeout)]
    [InlineData("\"selector\": \"#hidden\", \"state\": \"visible\", \"timeoutMilliseconds\": 300", ErrorTypes.ElementTimeout)]
    public async Task WaitForElement_Timeout_IsClassified(string properties, string errorType)
    {
        AssertFailed(await Run(Node("Browser.WaitForElement", properties)), errorType);
    }

    [Fact]
    public async Task WaitForElement_IsCancellable()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        var result = await host.RunAsync(host.Open() + ", " + Node("Browser.WaitForElement", "\"selector\": \"#never\", \"timeoutMilliseconds\": 60000"), cancellationToken: cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task SelectOption_ByValueOrLabel_AndMultiple()
    {
        var result = await Run(
            Node("Browser.SelectOption", "\"selector\": \"#color\", \"value\": \"'Green'\", \"to\": \"single\"") + ", "
            + Node("Browser.SelectOption", "\"selector\": \"#multi\", \"value\": \"['a', 'c']\", \"to\": \"many\""),
            "single", "many");

        AssertSucceeded(result);
        Assert.Equal(["g"], (IReadOnlyList<object?>)result.Outputs["single"]!);
        Assert.Equal(["a", "c"], (IReadOnlyList<object?>)result.Outputs["many"]!);
    }

    [Fact]
    public async Task InvalidTimeout_FailsWithInvalidArgument()
    {
        AssertFailed(await Run(Node("Browser.Click", "\"selector\": \"#greet\", \"timeoutMilliseconds\": 0")), ErrorTypes.InvalidArgument);
    }
}

/// <summary>The provisional Phase 4 selector syntax: css, xpath, text, role, chained steps and invalid selectors.</summary>
[Collection(BrowserTestGroup.Name)]
public sealed class SelectorTests(BrowserHost host) : IClassFixture<BrowserHost>
{
    [Theory]
    [InlineData("css=#title", "Welcome")]
    [InlineData("#title", "Welcome")]
    [InlineData("xpath=//h1", "Welcome")]
    [InlineData("//h1[@id='title']", "Welcome")]
    [InlineData("text=Welcome", "Welcome")]
    [InlineData("role=heading|Welcome", "Welcome")]
    [InlineData("role=link|Other page", "Other page")]
    [InlineData("css=ul >> text=Two", "Two")]
    [InlineData("xpath=//ul >> css=li:last-child", "Two")]
    public async Task SupportedSelectors_FindTheElement(string selector, string expected)
    {
        var result = await host.RunAsync(host.Open() + ", " + Node("Browser.GetText", $"\"selector\": \"{selector}\", \"to\": \"t\""), ["t"]);

        AssertSucceeded(result);
        Assert.Equal(expected, result.Outputs["t"]);
    }

    [Theory]
    [InlineData("role=notarole")]
    [InlineData("css=")]
    [InlineData("text= ")]
    [InlineData("css=#title >>  >> css=b")]
    [InlineData("css=##broken[")]
    [InlineData("xpath=//h1[")]
    public async Task InvalidSelectors_FailWithInvalidSelector(string selector)
    {
        var result = await host.RunAsync(host.Open() + ", " + Node("Browser.GetText", $"\"selector\": \"{selector}\", \"to\": \"t\", \"timeoutMilliseconds\": 1000"), ["t"]);

        AssertFailed(result, ErrorTypes.InvalidSelector);
    }
}
