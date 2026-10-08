using System.Security.Cryptography;
using Microsoft.Playwright;
using MyRPA.Browser.Contracts;

namespace MyRPA.Browser.Playwright.Recording;

/// <summary>
/// The browser recorder (ADR-0039): a visible Chromium whose pages get <c>recorder.js</c> and one binding with a
/// per-session random name. The only code in the plugin that adds a script to pages (an architecture test enforces it);
/// workflow runs never do.
/// </summary>
public sealed class PlaywrightRecorder(IBrowserProvider provider) : IBrowserRecorder, IAsyncDisposable
{
    private static readonly Lazy<string> _script = new(() =>
    {
        using var stream = typeof(PlaywrightRecorder).Assembly.GetManifestResourceStream("MyRPA.Browser.Playwright.Recording.recorder.js")
            ?? throw new InvalidOperationException("The recorder script is missing from the plugin.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private readonly List<PlaywrightRecordingSession> _sessions = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public async ValueTask<IRecordingSession> StartAsync(RecordingOptions options, IRecordingListener listener, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(listener);
        if (!options.StartUrl.IsAbsoluteUri || (options.StartUrl.Scheme != Uri.UriSchemeHttp && options.StartUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"'{options.StartUrl}' is not an absolute http or https URL.", nameof(options));
        }

        var driver = provider is PlaywrightBrowserProvider playwright
            ? await playwright.GetDriverAsync(cancellationToken).ConfigureAwait(false)
            : throw new InvalidOperationException("The recorder needs the Playwright browser provider.");
        var launch = new BrowserTypeLaunchOptions { Headless = options.Headless };
        if (options.DebuggingPort is { } port)
        {
            // Tests only (RecordingOptions.DebuggingPort): lets an end-to-end test act as the user over DevTools.
            launch.Args = [$"--remote-debugging-port={port}"];
        }

        var browser = await driver.Chromium.LaunchAsync(launch).WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = RandomNumberGenerator.GetHexString(16, lowercase: true);
            var binding = "__myrpa_" + RandomNumberGenerator.GetHexString(16, lowercase: true);
            var context = await browser.NewContextAsync(new BrowserNewContextOptions { AcceptDownloads = true }).ConfigureAwait(false);
            var session = new PlaywrightRecordingSession(id, options.StartUrl, browser, listener, Forget);
            await context.ExposeBindingAsync(binding, (BindingSource _, string message) => session.Receive(message)).ConfigureAwait(false);
            await context.AddInitScriptAsync(_script.Value.Replace("__MYRPA_BINDING__", binding, StringComparison.Ordinal)).ConfigureAwait(false);
            var page = await context.NewPageAsync().ConfigureAwait(false);
            await session.AttachAsync(page, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _sessions.Add(session);
            }

            return session;
        }
        catch
        {
            await browser.CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public string GenerateActivities(Uri startUrl, IReadOnlyList<RecordedStep> steps) => RecordedActivities.Generate(startUrl, steps);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        PlaywrightRecordingSession[] sessions;
        lock (_gate)
        {
            sessions = [.. _sessions];
        }

        foreach (var session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Forget(PlaywrightRecordingSession session)
    {
        lock (_gate)
        {
            _sessions.Remove(session);
        }
    }
}
