using Microsoft.Playwright;
using MyRPA.Sdk.Automation;
using MyRPA.Workflow.Execution;

namespace MyRPA.Browser.Playwright;

/// <summary>
/// A Playwright browser, its context and its single page. Every Playwright call observes the operation's timeout and
/// the run's cancellation token: when the token fires, the session stops waiting and the run's clean-up closes the
/// browser (Playwright calls themselves cannot be cancelled).
/// </summary>
public sealed class PlaywrightBrowserSession : IBrowserSession
{
    private readonly IBrowser _browser;
    private readonly IBrowserContext _context;
    private readonly IPage _page;
    private readonly TimeSpan _defaultTimeout;
    private int _closed;

    internal PlaywrightBrowserSession(BrowserSessionInfo info, IBrowser browser, IBrowserContext context, IPage page, TimeSpan defaultTimeout)
    {
        Info = info;
        _browser = browser;
        _context = context;
        _page = page;
        _defaultTimeout = defaultTimeout;
    }

    /// <inheritdoc />
    public BrowserSessionInfo Info { get; }

    /// <inheritdoc />
    public bool IsClosed => Volatile.Read(ref _closed) == 1 || !_browser.IsConnected || _page.IsClosed;

    /// <inheritdoc />
    public string Url => _page.Url;

    /// <inheritdoc />
    public async ValueTask NavigateAsync(Uri url, BrowserWaitUntil waitUntil, OperationLimits limits)
    {
        ArgumentNullException.ThrowIfNull(url);
        EnsureOpen();
        IResponse? response;
        try
        {
            response = await _page.GotoAsync(url.AbsoluteUri, new PageGotoOptions { Timeout = limits.Milliseconds, WaitUntil = Map(waitUntil) })
                .WaitAsync(limits.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (BrowserErrors.IsPlaywrightFailure(ex))
        {
            throw BrowserErrors.ForNavigation(ex, url, limits);
        }

        if (response is { Status: >= 400 } failed)
        {
            throw new ActivityFailedException(BrowserErrorTypes.NavigationFailed, $"Navigating to {url} returned HTTP {failed.Status} {failed.StatusText}.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<IBrowserElement> LocateAsync(Selector selector, OperationLimits limits)
    {
        await WaitForAsync(selector, BrowserElementState.Attached, limits).ConfigureAwait(false);
        return new PlaywrightElement(BrowserSelectors.ToLocator(_page, selector), BrowserSelectors.Format(selector), limits.Timeout);
    }

    /// <inheritdoc />
    public async ValueTask WaitForAsync(Selector selector, BrowserElementState state, OperationLimits limits)
    {
        ArgumentNullException.ThrowIfNull(selector);
        EnsureOpen();
        var locator = BrowserSelectors.ToLocator(_page, selector);
        var text = BrowserSelectors.Format(selector);
        try
        {
            // WaitFor is strict: more than one match fails with a strict mode violation (AmbiguousMatch).
            await locator.WaitForAsync(new LocatorWaitForOptions { State = Map(state), Timeout = limits.Milliseconds })
                .WaitAsync(limits.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (BrowserErrors.IsPlaywrightFailure(ex))
        {
            if (ex is TimeoutException && state is BrowserElementState.Hidden or BrowserElementState.Detached)
            {
                throw new ActivityFailedException(BrowserErrorTypes.ElementTimeout, $"The element '{text}' did not become {state.ToString().ToLowerInvariant()} within {limits.Milliseconds:0} ms.", ex);
            }

            throw await BrowserErrors.ForElementAsync(ex, locator, text, $"become {state.ToString().ToLowerInvariant()}", limits).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<string> DownloadAsync(Selector trigger, string destinationPath, OperationLimits limits)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        EnsureOpen();
        var locator = BrowserSelectors.ToLocator(_page, trigger);
        var text = BrowserSelectors.Format(trigger);
        var waitForDownload = _page.WaitForDownloadAsync(new PageWaitForDownloadOptions { Timeout = limits.Milliseconds });
        try
        {
            await locator.ClickAsync(new LocatorClickOptions { Timeout = limits.Milliseconds }).WaitAsync(limits.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (BrowserErrors.IsPlaywrightFailure(ex))
        {
            _ = waitForDownload.ContinueWith(t => t.Exception, TaskScheduler.Default); // observe the abandoned wait
            throw await BrowserErrors.ForElementAsync(ex, locator, text, "click", limits).ConfigureAwait(false);
        }

        try
        {
            var download = await waitForDownload.WaitAsync(limits.CancellationToken).ConfigureAwait(false);
            if (await download.FailureAsync().WaitAsync(limits.CancellationToken).ConfigureAwait(false) is { } failure)
            {
                throw new ActivityFailedException(BrowserErrorTypes.DownloadFailed, $"The download started by '{text}' failed: {failure}");
            }

            await download.SaveAsAsync(destinationPath).WaitAsync(limits.CancellationToken).ConfigureAwait(false);
            return download.SuggestedFilename;
        }
        catch (Exception ex) when (BrowserErrors.IsPlaywrightFailure(ex))
        {
            throw ex is TimeoutException
                ? new ActivityFailedException(BrowserErrorTypes.DownloadFailed, $"Clicking '{text}' did not start a download within {limits.Milliseconds:0} ms.", ex)
                : BrowserErrors.ForOperation(ex, $"Downloading from '{text}'", limits);
        }
    }

    /// <inheritdoc />
    public async ValueTask CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        try
        {
            // Closing the context closes its page and discards downloads; closing the browser ends its process.
            await _context.CloseAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // Already closed or disconnected; still close the browser below.
        }

        try
        {
            await _browser.CloseAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // The browser is already gone.
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => CloseAsync();

    private void EnsureOpen()
    {
        if (IsClosed)
        {
            throw BrowserErrors.Closed();
        }
    }

    private static WaitUntilState Map(BrowserWaitUntil waitUntil) => waitUntil switch
    {
        BrowserWaitUntil.DomContentLoaded => WaitUntilState.DOMContentLoaded,
        BrowserWaitUntil.NetworkIdle => WaitUntilState.NetworkIdle,
        BrowserWaitUntil.Commit => WaitUntilState.Commit,
        _ => WaitUntilState.Load,
    };

    private static WaitForSelectorState Map(BrowserElementState state) => state switch
    {
        BrowserElementState.Attached => WaitForSelectorState.Attached,
        BrowserElementState.Hidden => WaitForSelectorState.Hidden,
        BrowserElementState.Detached => WaitForSelectorState.Detached,
        _ => WaitForSelectorState.Visible,
    };
}
