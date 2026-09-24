using Microsoft.Playwright;
using MyRPA.Sdk.Automation;
using MyRPA.Workflow.Execution;

namespace MyRPA.Browser.Playwright;

/// <summary>
/// The Playwright implementation of <see cref="IBrowserProvider"/>. Plugin lifetime: it owns one Playwright driver
/// (a Node.js process started on first use and stopped when the host shuts down). Every session gets its own browser
/// process, context and page, so concurrent runs never share browser state. Thread-safe.
/// </summary>
public sealed class PlaywrightBrowserProvider : IBrowserProvider, IAsyncDisposable, IDisposable
{
    /// <summary>The Microsoft.Playwright version this provider is built and tested with.</summary>
    public const string PlaywrightVersion = "1.63.0";

    private readonly SemaphoreSlim _driverGate = new(1, 1);
    private IPlaywright? _playwright;
    private bool _disposed;

    /// <summary>The provider id declared in the manifest.</summary>
    public static AutomationProviderId Id { get; } = new("Browser.Playwright");

    /// <inheritdoc />
    public AutomationProviderDescriptor Descriptor { get; } =
        new(Id, "Playwright browser", "Browser", $"Chromium automation through Microsoft Playwright {PlaywrightVersion}.");

    /// <inheritdoc />
    public async ValueTask<IBrowserSession> LaunchAsync(string sessionId, BrowserLaunchOptions options, OperationLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(options.BrowserName, BrowserLaunchOptions.Chromium, StringComparison.Ordinal))
        {
            throw new ActivityFailedException(BrowserErrorTypes.BrowserLaunchFailed, $"Browser '{options.BrowserName}' is not supported; use 'chromium'.");
        }

        var playwright = await GetDriverAsync(limits.CancellationToken).ConfigureAwait(false);
        var launch = playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = options.Headless, Timeout = limits.Milliseconds });
        IBrowser browser;
        try
        {
            browser = await launch.WaitAsync(limits.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The launch keeps running in the driver; close the browser as soon as it exists so no process is orphaned.
            _ = CloseWhenLaunchedAsync(launch);
            throw;
        }
        catch (Exception ex) when (BrowserErrors.IsPlaywrightFailure(ex))
        {
            throw BrowserErrors.ForLaunch(ex, options);
        }

        try
        {
            var context = await browser.NewContextAsync(new BrowserNewContextOptions { AcceptDownloads = true }).WaitAsync(limits.CancellationToken).ConfigureAwait(false);
            var page = await context.NewPageAsync().WaitAsync(limits.CancellationToken).ConfigureAwait(false);
            page.SetDefaultTimeout((float)options.EffectiveDefaultTimeout.TotalMilliseconds);
            var info = new BrowserSessionInfo(sessionId, options.BrowserName, browser.Version, options.Headless, DateTimeOffset.UtcNow);
            return new PlaywrightBrowserSession(info, browser, context, page, options.EffectiveDefaultTimeout);
        }
        catch (Exception ex)
        {
            await browser.CloseAsync().ConfigureAwait(false);
            if (ex is PlaywrightException playwrightException)
            {
                throw BrowserErrors.ForLaunch(playwrightException, options);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops the Playwright driver (and with it any browser still running).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _playwright?.Dispose();
        _driverGate.Dispose();
    }

    private async Task<IPlaywright> GetDriverAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _driverGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _playwright ??= await Microsoft.Playwright.Playwright.CreateAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (BrowserErrors.IsPlaywrightFailure(ex))
        {
            throw new AutomationException(
                AutomationErrorTypes.ProviderUnavailable,
                $"The Playwright driver could not start: {ex.Message.Split('\n', 2)[0]}",
                ex);
        }
        finally
        {
            _driverGate.Release();
        }
    }

    private static async Task CloseWhenLaunchedAsync(Task<IBrowser> launch)
    {
        try
        {
            var browser = await launch.ConfigureAwait(false);
            await browser.CloseAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // The launch failed or the driver is gone: there is no browser to close.
        }
    }
}
