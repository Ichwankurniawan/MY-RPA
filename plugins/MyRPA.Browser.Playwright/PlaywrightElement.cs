using Microsoft.Playwright;
using MyRPA.Sdk.Automation;

namespace MyRPA.Browser.Playwright;

/// <summary>
/// A browser element backed by a Playwright locator. Every operation auto-waits (up to the element's timeout) for the
/// element to be actionable, is strict (more than one match fails with <c>AmbiguousMatch</c>) and observes the token.
/// </summary>
public sealed class PlaywrightElement : IBrowserElement
{
    private readonly ILocator _locator;
    private readonly TimeSpan _timeout;

    internal PlaywrightElement(ILocator locator, string description, TimeSpan timeout)
    {
        _locator = locator;
        Description = description;
        _timeout = timeout;
    }

    /// <inheritdoc />
    public AutomationProviderId Provider => PlaywrightBrowserProvider.Id;

    /// <inheritdoc />
    public string Description { get; }

    private float Milliseconds => (float)Math.Max(1, _timeout.TotalMilliseconds);

    /// <inheritdoc />
    public ValueTask ClickAsync(CancellationToken cancellationToken) =>
        RunAsync(() => _locator.ClickAsync(new LocatorClickOptions { Timeout = Milliseconds }), "click", cancellationToken);

    /// <inheritdoc />
    public ValueTask TypeTextAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return RunAsync(() => _locator.FillAsync(text, new LocatorFillOptions { Timeout = Milliseconds }), "type into", cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask AppendTextAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return RunAsync(() => _locator.PressSequentiallyAsync(text, new LocatorPressSequentiallyOptions { Timeout = Milliseconds }), "type into", cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<string> GetTextAsync(CancellationToken cancellationToken) =>
        RunAsync(() => _locator.InnerTextAsync(new LocatorInnerTextOptions { Timeout = Milliseconds }), "read the text of", cancellationToken);

    /// <inheritdoc />
    public ValueTask<string?> GetAttributeAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return RunAsync(() => _locator.GetAttributeAsync(name, new LocatorGetAttributeOptions { Timeout = Milliseconds }), "read an attribute of", cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> SelectOptionsAsync(IReadOnlyList<string> values, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        return await RunAsync(() => _locator.SelectOptionAsync(values, new LocatorSelectOptionOptions { Timeout = Milliseconds }), "select options of", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask SetInputFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return RunAsync(() => _locator.SetInputFilesAsync(paths, new LocatorSetInputFilesOptions { Timeout = Milliseconds }), "set files of", cancellationToken);
    }

    private async ValueTask RunAsync(Func<Task> operation, string action, CancellationToken cancellationToken)
    {
        try
        {
            await operation().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (BrowserErrors.IsPlaywrightFailure(ex))
        {
            throw await BrowserErrors.ForElementAsync(ex, _locator, Description, action, new OperationLimits(_timeout, cancellationToken)).ConfigureAwait(false);
        }
    }

    private async ValueTask<T> RunAsync<T>(Func<Task<T>> operation, string action, CancellationToken cancellationToken)
    {
        try
        {
            return await operation().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (BrowserErrors.IsPlaywrightFailure(ex))
        {
            throw await BrowserErrors.ForElementAsync(ex, _locator, Description, action, new OperationLimits(_timeout, cancellationToken)).ConfigureAwait(false);
        }
    }
}
