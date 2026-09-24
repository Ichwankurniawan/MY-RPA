using System.Globalization;
using Microsoft.Playwright;
using MyRPA.Sdk.Automation;
using MyRPA.Workflow.Execution;

namespace MyRPA.Browser.Playwright;

/// <summary>
/// Maps Playwright failures to MyRPA failures (ADR-0017). The MyRPA error type and message are the contract; the
/// Playwright exception is kept only as the inner exception, for logs.
/// </summary>
internal static class BrowserErrors
{
    public static Exception ForLaunch(Exception ex, BrowserLaunchOptions options)
    {
        if (ex.Message.Contains("Executable doesn't exist", StringComparison.Ordinal))
        {
            return new AutomationException(
                AutomationErrorTypes.ProviderUnavailable,
                $"The {options.BrowserName} browser for Playwright {PlaywrightBrowserProvider.PlaywrightVersion} is not installed. " +
                "Run: pwsh <plugin directory>/playwright.ps1 install chromium",
                ex);
        }

        return ex is TimeoutException
            ? new AutomationException(AutomationErrorTypes.OperationTimeout, $"Launching {options.BrowserName} timed out.", ex)
            : new ActivityFailedException(BrowserErrorTypes.BrowserLaunchFailed, $"Launching {options.BrowserName} failed: {FirstLine(ex.Message)}", ex);
    }

    public static Exception ForNavigation(Exception ex, Uri url, OperationLimits limits) => ex switch
    {
        TimeoutException => new AutomationException(AutomationErrorTypes.OperationTimeout, $"Navigating to {url} did not complete within {Ms(limits)} ms.", ex),
        _ when IsClosed(ex) => Closed(ex),
        _ => new ActivityFailedException(BrowserErrorTypes.NavigationFailed, $"Navigating to {url} failed: {FirstLine(ex.Message)}", ex),
    };

    /// <summary>Maps a failed element operation; distinguishes "never matched" from "matched but not ready".</summary>
    public static async Task<Exception> ForElementAsync(Exception ex, ILocator locator, string selector, string action, OperationLimits limits)
    {
        if (IsClosed(ex))
        {
            return Closed(ex);
        }

        if (IsInvalidSelector(ex))
        {
            return new AutomationException(AutomationErrorTypes.InvalidSelector, $"Invalid browser selector '{selector}': {FirstLine(ex.Message)}", ex);
        }

        if (ex.Message.Contains("strict mode violation", StringComparison.OrdinalIgnoreCase))
        {
            return new AutomationException(AutomationErrorTypes.AmbiguousMatch, $"More than one element matches '{selector}'; a browser selector must match exactly one element.", ex);
        }

        if (ex is TimeoutException)
        {
            var count = await CountQuietlyAsync(locator).ConfigureAwait(false);
            return count == 0
                ? new AutomationException(AutomationErrorTypes.ElementNotFound, $"No element matches '{selector}' (waited {Ms(limits)} ms to {action}).", ex)
                : new ActivityFailedException(BrowserErrorTypes.ElementTimeout, $"The element '{selector}' was found but was not ready to {action} within {Ms(limits)} ms.", ex);
        }

        return new ActivityFailedException(BrowserErrorTypes.BrowserError, $"Cannot {action} '{selector}': {FirstLine(ex.Message)}", ex);
    }

    public static Exception ForOperation(Exception ex, string what, OperationLimits limits) => ex switch
    {
        TimeoutException => new AutomationException(AutomationErrorTypes.OperationTimeout, $"{what} did not complete within {Ms(limits)} ms.", ex),
        _ when IsClosed(ex) => Closed(ex),
        _ => new ActivityFailedException(BrowserErrorTypes.BrowserError, $"{what} failed: {FirstLine(ex.Message)}", ex),
    };

    /// <summary>
    /// Playwright reports failures as <see cref="PlaywrightException"/> (including its internal target-closed exception)
    /// and timeouts as <see cref="System.TimeoutException"/>, which is not a <see cref="PlaywrightException"/>.
    /// </summary>
    public static bool IsPlaywrightFailure(Exception ex) => ex is PlaywrightException or TimeoutException;

    public static ActivityFailedException Closed(Exception? inner = null) =>
        new(BrowserErrorTypes.SessionClosed, "The browser session is closed.", inner);

    private static bool IsClosed(Exception ex) =>
        ex.Message.Contains("has been closed", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Target closed", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Browser closed", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Connection closed", StringComparison.OrdinalIgnoreCase);

    private static bool IsInvalidSelector(Exception ex) =>
        ex.Message.Contains("is not a valid selector", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Unexpected token", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("SyntaxError", StringComparison.Ordinal)
        || ex.Message.Contains("is not a valid XPath", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Unknown engine", StringComparison.OrdinalIgnoreCase);

    private static async Task<int> CountQuietlyAsync(ILocator locator)
    {
        try
        {
            return await locator.CountAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            return 0;
        }
    }

    private static string Ms(OperationLimits limits) => limits.Milliseconds.ToString("0", CultureInfo.InvariantCulture);

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length > 300 ? line[..300] + "…" : line;
    }
}
