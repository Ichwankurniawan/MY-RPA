using MyRPA.Core.Execution;
using static MyRPA.Browser.Playwright.Tests.Nodes;

namespace MyRPA.Browser.Playwright.Tests;

/// <summary>Sessions: launch, context, page, close and clean-up at the end of the run.</summary>
[Collection(BrowserTestGroup.Name)]
public sealed class BrowserLifecycleTests(BrowserHost host) : IClassFixture<BrowserHost>
{
    [Fact]
    public async Task EndToEnd_OpenNavigateLocateClickTypeReadClose()
    {
        var result = await host.RunAsync(
            string.Join(", ",
                Node("Browser.Open", $"\"url\": \"'{host.Site.Url("/other")}'\", \"to\": \"session\""),
                Node("Browser.Navigate", $"\"url\": \"'{host.Site.BaseUrl}'\", \"session\": \"session\""),
                Node("Browser.WaitForElement", "\"selector\": \"css=#name\""),
                Node("Browser.TypeText", "\"selector\": \"css=#name\", \"text\": \"'Ada'\""),
                Node("Browser.Click", "\"selector\": \"role=button|Greet\""),
                Node("Browser.GetText", "\"selector\": \"#out\", \"to\": \"greeting\""),
                Node("Browser.Close", "\"session\": \"session\"")),
            ["session", "greeting"]);

        AssertSucceeded(result);
        Assert.Equal("browser-1", result.Outputs["session"]);
        Assert.Equal("Hello, Ada", result.Outputs["greeting"]);
    }

    [Fact]
    public async Task SessionsLeftOpen_AreClosedWhenTheRunEnds()
    {
        var baseline = BrowserProcesses.Count();

        var result = await host.RunAsync(host.Open() + ", " + Node("Browser.Open", "\"url\": \"'about:blank'\""));

        AssertSucceeded(result);
        Assert.Equal(baseline, await BrowserProcesses.SettleAsync(baseline));
    }

    [Fact]
    public async Task FailedRun_StillClosesItsBrowser()
    {
        var baseline = BrowserProcesses.Count();

        var result = await host.RunAsync(host.Open() + ", " + Node("Core.Throw", "\"message\": \"'boom'\""));

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(baseline, await BrowserProcesses.SettleAsync(baseline));
    }

    [Fact]
    public async Task NoOpenSession_FailsWithSessionNotFound()
    {
        AssertFailed(await host.RunAsync(Node("Browser.Click", "\"selector\": \"#greet\"")), ErrorTypes.SessionNotFound);
    }

    [Fact]
    public async Task UsingAClosedSession_FailsWithSessionNotFound()
    {
        var result = await host.RunAsync(host.Open() + ", " + Node("Browser.Close", "") + ", " + Node("Browser.Click", "\"selector\": \"#greet\""));

        AssertFailed(result, ErrorTypes.SessionNotFound);
    }

    [Fact]
    public async Task TwoSessions_NeedAnExplicitSession_AndAreIndependent()
    {
        var ambiguous = await host.RunAsync(host.Open(id: "a") + ", " + host.Open(path: "/other", id: "b") + ", " + Node("Browser.GetText", "\"selector\": \"#title\", \"to\": \"t\""), ["t"]);
        AssertFailed(ambiguous, ErrorTypes.SessionNotFound);

        var result = await host.RunAsync(
            string.Join(", ",
                Node("Browser.Open", $"\"url\": \"'{host.Site.BaseUrl}'\", \"to\": \"first\""),
                Node("Browser.Open", $"\"url\": \"'{host.Site.Url("/other")}'\", \"to\": \"second\""),
                Node("Browser.GetText", "\"selector\": \"#title\", \"session\": \"first\", \"to\": \"a\""),
                Node("Browser.GetText", "\"selector\": \"#title\", \"session\": \"second\", \"to\": \"b\"")),
            ["first", "second", "a", "b"]);

        AssertSucceeded(result);
        Assert.Equal("Welcome", result.Outputs["a"]);
        Assert.Equal("Other", result.Outputs["b"]);
        Assert.NotEqual(result.Outputs["first"], result.Outputs["second"]);
    }

    [Fact]
    public async Task UnknownSessionId_FailsWithSessionNotFound()
    {
        var result = await host.RunAsync(host.Open() + ", " + Node("Browser.Click", "\"selector\": \"#greet\", \"session\": \"'browser-99'\""));

        AssertFailed(result, ErrorTypes.SessionNotFound);
    }

    [Fact]
    public async Task UnsupportedBrowser_IsRejectedByValidation()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => host.RunAsync(Node("Browser.Open", "\"browser\": \"firefox\"")));
    }
}

/// <summary>Navigation: valid, invalid, HTTP and network failures, timeout, run timeout and cancellation.</summary>
[Collection(BrowserTestGroup.Name)]
public sealed class NavigationTests(BrowserHost host) : IClassFixture<BrowserHost>
{
    [Fact]
    public async Task Navigate_ToAPage_LoadsIt()
    {
        var result = await host.RunAsync(host.Open() + ", " + Node("Browser.Navigate", $"\"url\": \"'{host.Site.Url("/other")}'\"")
            + ", " + Node("Browser.GetText", "\"selector\": \"#title\", \"to\": \"t\""), ["t"]);

        AssertSucceeded(result);
        Assert.Equal("Other", result.Outputs["t"]);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.invalid/file")]
    [InlineData("/relative/path")]
    public async Task InvalidOrForbiddenUrl_FailsWithInvalidUrl(string url)
    {
        var result = await host.RunAsync(host.Open() + ", " + Node("Browser.Navigate", $"\"url\": \"'{url}'\""));

        AssertFailed(result, ErrorTypes.InvalidUrl);
    }

    [Fact]
    public async Task HttpErrorStatus_FailsWithNavigationFailed()
    {
        var result = await host.RunAsync(host.Open() + ", " + Node("Browser.Navigate", $"\"url\": \"'{host.Site.Url("/missing")}'\""));

        AssertFailed(result, ErrorTypes.NavigationFailed);
        Assert.Contains("HTTP 404", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreachableServer_FailsWithNavigationFailed()
    {
        var result = await host.RunAsync(host.Open() + ", " + Node("Browser.Navigate", "\"url\": \"'http://localhost:1/'\""));

        AssertFailed(result, ErrorTypes.NavigationFailed);
    }

    [Fact]
    public async Task SlowPage_WithNodeTimeout_FailsWithOperationTimeout()
    {
        var result = await host.RunAsync(host.Open() + ", " + Node("Browser.Navigate", $"\"url\": \"'{host.Site.Url("/slow")}'\", \"timeoutMilliseconds\": 500"));

        AssertFailed(result, ErrorTypes.OperationTimeout);
    }

    [Fact]
    public async Task SlowPage_WithRunTimeout_EndsTheRunAsTimedOut()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await host.RunAsync(host.Open() + ", " + Node("Browser.Navigate", $"\"url\": \"'{host.Site.Url("/slow")}'\""), timeout: TimeSpan.FromSeconds(3));

        Assert.Equal(ExecutionStatus.TimedOut, result.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"Took {watch.Elapsed}.");
    }

    [Fact]
    public async Task Cancellation_StopsTheRunPromptly_AndClosesTheBrowser()
    {
        var baseline = BrowserProcesses.Count();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await host.RunAsync(host.Open() + ", " + Node("Browser.Navigate", $"\"url\": \"'{host.Site.Url("/slow")}'\""), cancellationToken: cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"Took {watch.Elapsed}.");
        Assert.Equal(baseline, await BrowserProcesses.SettleAsync(baseline));
    }
}
