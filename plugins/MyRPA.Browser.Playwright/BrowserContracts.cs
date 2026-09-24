using MyRPA.Sdk.Automation;

namespace MyRPA.Browser.Playwright;

/// <summary>
/// The browser technology interface (PRD 7.2: <c>IBrowserProvider → PlaywrightProvider</c>). Browser activities depend on
/// this interface; <see cref="PlaywrightBrowserProvider"/> implements it. Plugin lifetime: one instance shared by every
/// run, owning the Playwright driver process.
/// </summary>
public interface IBrowserProvider : IAutomationProvider
{
    /// <summary>
    /// Launches a new browser with its own context and page. Every session is a separate browser process: sessions
    /// never share cookies, storage or pages.
    /// </summary>
    /// <param name="sessionId">Identifier of the new session, unique within the run.</param>
    /// <param name="options">Browser to launch.</param>
    /// <param name="limits">Launch timeout and cancellation.</param>
    ValueTask<IBrowserSession> LaunchAsync(string sessionId, BrowserLaunchOptions options, OperationLimits limits);
}

/// <summary>
/// One browser, its context and its page (ADR-0017). Owned by the run that opened it (<see cref="BrowserSessions"/>) and
/// closed when the run ends. Elements are found with SDK <see cref="Selector"/>s; activities wait for elements (Playwright
/// auto-waiting), so the SDK's immediate <see cref="ISelectorResolver"/> is not implemented until a use needs it.
/// </summary>
public interface IBrowserSession : IAsyncDisposable
{
    /// <summary>Session metadata.</summary>
    BrowserSessionInfo Info { get; }

    /// <summary>Whether the session was closed or its browser disconnected.</summary>
    bool IsClosed { get; }

    /// <summary>The current page URL.</summary>
    string Url { get; }

    /// <summary>Navigates the page. HTTP error statuses (400 and above) and network errors fail with <c>NavigationFailed</c>.</summary>
    /// <param name="url">Absolute <c>http</c>, <c>https</c> or <c>about:blank</c> URL.</param>
    /// <param name="waitUntil">When navigation is considered complete.</param>
    /// <param name="limits">Timeout and cancellation.</param>
    ValueTask NavigateAsync(Uri url, BrowserWaitUntil waitUntil, OperationLimits limits);

    /// <summary>
    /// Waits until exactly one element matches <paramref name="selector"/> (attached to the page) and returns it.
    /// Fails with <c>ElementNotFound</c>, <c>AmbiguousMatch</c> or <c>InvalidSelector</c>.
    /// </summary>
    /// <param name="selector">A browser selector (<see cref="BrowserSelectors"/>).</param>
    /// <param name="limits">Timeout for this and the returned element's operations, and cancellation.</param>
    ValueTask<IBrowserElement> LocateAsync(Selector selector, OperationLimits limits);

    /// <summary>Waits until the element reaches <paramref name="state"/>.</summary>
    /// <param name="selector">A browser selector.</param>
    /// <param name="state">State to wait for.</param>
    /// <param name="limits">Timeout and cancellation.</param>
    ValueTask WaitForAsync(Selector selector, BrowserElementState state, OperationLimits limits);

    /// <summary>
    /// Clicks the element matched by <paramref name="trigger"/>, waits for the download it starts and saves it to
    /// <paramref name="destinationPath"/> (already checked against the file policy by the caller).
    /// </summary>
    /// <param name="trigger">The element that starts the download.</param>
    /// <param name="destinationPath">Full destination path.</param>
    /// <param name="limits">Timeout and cancellation.</param>
    /// <returns>The file name suggested by the server.</returns>
    ValueTask<string> DownloadAsync(Selector trigger, string destinationPath, OperationLimits limits);

    /// <summary>Closes the page, context and browser. Idempotent.</summary>
    ValueTask CloseAsync();
}

/// <summary>A browser element. Operations use the timeout the element was located with.</summary>
public interface IBrowserElement : IAutomationElement
{
    /// <summary>Types <paramref name="text"/> key by key after the current content (no clearing).</summary>
    /// <param name="text">Text to type.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask AppendTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>Selects options of a <c>select</c> element by value or label; returns the selected values.</summary>
    /// <param name="values">Option values or labels.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<IReadOnlyList<string>> SelectOptionsAsync(IReadOnlyList<string> values, CancellationToken cancellationToken);

    /// <summary>Sets the files of an <c>input type=file</c> element.</summary>
    /// <param name="paths">Full paths, already checked against the file policy.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask SetInputFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken);
}

/// <summary>Timeout and cancellation of one browser operation.</summary>
/// <param name="Timeout">Maximum time Playwright may spend (already limited by the run's deadline).</param>
/// <param name="CancellationToken">The run's token.</param>
public readonly record struct OperationLimits(TimeSpan Timeout, CancellationToken CancellationToken)
{
    /// <summary>The timeout in milliseconds, as Playwright expects it.</summary>
    public float Milliseconds => (float)Math.Max(1, Timeout.TotalMilliseconds);
}

/// <summary>Which browser to launch.</summary>
/// <param name="BrowserName">Only <c>chromium</c> is supported and tested in Phase 4.</param>
/// <param name="Headless">Run without a visible window (default).</param>
/// <param name="DefaultTimeout">Timeout for page operations that are not given one (e.g. elements from <c>ResolveAsync</c>).</param>
public sealed record BrowserLaunchOptions(string BrowserName = BrowserLaunchOptions.Chromium, bool Headless = true, TimeSpan? DefaultTimeout = null)
{
    /// <summary>The effective default timeout (30 seconds unless set).</summary>
    public TimeSpan EffectiveDefaultTimeout => DefaultTimeout ?? TimeSpan.FromSeconds(30);

    /// <summary>The Chromium browser.</summary>
    public const string Chromium = "chromium";
}

/// <summary>Metadata of a browser session.</summary>
/// <param name="Id">Session id within the run (e.g. <c>browser-1</c>).</param>
/// <param name="BrowserName">Browser engine.</param>
/// <param name="BrowserVersion">Browser version reported by the browser.</param>
/// <param name="Headless">Whether the browser has no window.</param>
/// <param name="OpenedAt">When the session was opened.</param>
public sealed record BrowserSessionInfo(string Id, string BrowserName, string BrowserVersion, bool Headless, DateTimeOffset OpenedAt);

/// <summary>When a navigation is complete.</summary>
public enum BrowserWaitUntil
{
    /// <summary>The <c>load</c> event fired (default).</summary>
    Load = 0,

    /// <summary>The <c>DOMContentLoaded</c> event fired.</summary>
    DomContentLoaded = 1,

    /// <summary>No network connections for 500 ms.</summary>
    NetworkIdle = 2,

    /// <summary>The response was received and the document started loading.</summary>
    Commit = 3,
}

/// <summary>Element states for <c>Browser.WaitForElement</c>.</summary>
public enum BrowserElementState
{
    /// <summary>Present in the DOM.</summary>
    Attached = 0,

    /// <summary>Present and visible.</summary>
    Visible = 1,

    /// <summary>Not visible or not present.</summary>
    Hidden = 2,

    /// <summary>Not present in the DOM.</summary>
    Detached = 3,
}

/// <summary>
/// Browser-specific failure classifications, in addition to <see cref="AutomationErrorTypes"/> (ADR-0017). They are the
/// node's <c>errorType</c>, so workflows can handle them in <c>Core.TryCatch</c>; keep them stable.
/// </summary>
public static class BrowserErrorTypes
{
    /// <summary>The browser could not be launched (after the browser and driver were found).</summary>
    public const string BrowserLaunchFailed = "BrowserLaunchFailed";

    /// <summary>A navigation failed: network error, HTTP status 400 or above, or aborted navigation.</summary>
    public const string NavigationFailed = "NavigationFailed";

    /// <summary>The URL is not an absolute http, https or about:blank URL.</summary>
    public const string InvalidUrl = "InvalidUrl";

    /// <summary>Elements match the selector but did not become ready (visible, enabled, stable) in time.</summary>
    public const string ElementTimeout = "ElementTimeout";

    /// <summary>The session was closed or its browser disconnected.</summary>
    public const string SessionClosed = "SessionClosed";

    /// <summary>No open session has the given id, or no session (or more than one) is open when none is named.</summary>
    public const string SessionNotFound = "SessionNotFound";

    /// <summary>A download did not complete.</summary>
    public const string DownloadFailed = "DownloadFailed";

    /// <summary>A file path is outside the plugin's file root or is a link.</summary>
    public const string FileAccessDenied = "FileAccessDenied";

    /// <summary>A file to upload does not exist.</summary>
    public const string FileNotFound = "FileNotFound";

    /// <summary>A download destination exists and <c>overwrite</c> is false.</summary>
    public const string FileAlreadyExists = "FileAlreadyExists";

    /// <summary>An activity property has an invalid value (for example a non-positive timeout).</summary>
    public const string InvalidArgument = "InvalidArgument";

    /// <summary>Any other browser failure.</summary>
    public const string BrowserError = "BrowserError";
}
