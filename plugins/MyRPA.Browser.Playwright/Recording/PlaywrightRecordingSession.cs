using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Playwright;
using MyRPA.Browser.Contracts;
using MyRPA.Sdk.Automation;

namespace MyRPA.Browser.Playwright.Recording;

/// <summary>
/// One recording (ADR-0039): turns validated recorder messages, navigations and downloads into steps, in order, on one
/// background loop. Selectors are re-checked with the browser (exactly one match) while the page is still there.
/// </summary>
public sealed class PlaywrightRecordingSession : IRecordingSession, IRecordingTestDriver
{
    /// <summary>A navigation this soon after a recorded click or Enter is that action's result, not a Navigate step.</summary>
    private static readonly TimeSpan _actionNavigation = TimeSpan.FromMilliseconds(2000);

    private readonly IBrowser _browser;
    private readonly IRecordingListener _listener;
    private readonly Action<PlaywrightRecordingSession> _forget;
    private readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<RecordedStep> _steps = [];
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IPage? _page;
    private int _sequence;
    private int _ended;
    private bool _started;
    private long _lastActionAt;
    private (int Element, int Sequence)? _lastType;
    private RecordedStep? _lastClick;

    internal PlaywrightRecordingSession(string id, Uri startUrl, IBrowser browser, IRecordingListener listener, Action<PlaywrightRecordingSession> forget)
    {
        Id = id;
        StartUrl = startUrl;
        _browser = browser;
        _listener = listener;
        _forget = forget;
        _ = Task.Run(ProcessAsync);
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public Uri StartUrl { get; }

    /// <inheritdoc />
    public IReadOnlyList<RecordedStep> Steps
    {
        get
        {
            lock (_gate)
            {
                return [.. _steps];
            }
        }
    }

    /// <inheritdoc />
    public Task Completion => _completion.Task;

    internal async Task AttachAsync(IPage page, CancellationToken cancellationToken)
    {
        _page = page;
        page.FrameNavigated += (_, frame) =>
        {
            if (frame == page.MainFrame)
            {
                var at = Stopwatch.GetTimestamp();
                var url = frame.Url;
                _work.Writer.TryWrite(() => NavigatedAsync(url, at));
            }
        };
        page.Download += (_, download) => _work.Writer.TryWrite(() => DownloadedAsync(download));
        page.Close += (_, _) => End(new RecordingEnd(RecordingEndReason.BrowserClosed));
        _browser.Disconnected += (_, _) => End(new RecordingEnd(RecordingEndReason.BrowserClosed));
        await page.GotoAsync(StartUrl.AbsoluteUri).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A message from the page's binding (any thread): queued, validated in order.</summary>
    internal void Receive(string message)
    {
        var at = Stopwatch.GetTimestamp();
        _work.Writer.TryWrite(() => MessageAsync(message, at));
    }

    /// <inheritdoc />
    public async ValueTask StopAsync()
    {
        End(new RecordingEnd(RecordingEndReason.Stopped));
        await _browser.CloseAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _ended) == 0)
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync()
    {
        await foreach (var item in _work.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await item().ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                // The page went away while a step was being checked: that step uses the script's own judgment.
            }
        }
    }

    private async Task MessageAsync(string json, long at)
    {
        if (RecorderMessage.Parse(json) is not { } message)
        {
            return;
        }

        var (selector, alternatives) = await ChooseSelectorsAsync(message.Candidates).ConfigureAwait(false);
        if (message.Kind == RecordedStepKind.Click)
        {
            _lastActionAt = at;
        }

        var replaces = message.Kind == RecordedStepKind.Type && _lastType is { } last && last.Element == message.Element ? last.Sequence : (int?)null;
        var step = Emit(sequence => new RecordedStep(sequence, message.Kind, DateTimeOffset.UtcNow)
        {
            Selector = selector,
            Alternatives = alternatives,
            Element = message.Label,
            Text = message.Text,
            Secret = message.Secret,
            Values = message.Values,
            Replaces = replaces,
        });
        _lastType = message.Kind == RecordedStepKind.Type ? (message.Element, step.Sequence) : null;
        _lastClick = message.Kind == RecordedStepKind.Click ? step : null;
    }

    private Task NavigatedAsync(string url, long at)
    {
        // The start page, and pages a recorded click led to, are not Navigate steps.
        var first = !_started;
        _started = true;
        if (first || Stopwatch.GetElapsedTime(_lastActionAt, at) < _actionNavigation || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Task.CompletedTask;
        }

        _lastType = null;
        _lastClick = null;
        Emit(sequence => new RecordedStep(sequence, RecordedStepKind.Navigate, DateTimeOffset.UtcNow) { Url = uri.AbsoluteUri });
        return Task.CompletedTask;
    }

    private async Task DownloadedAsync(IDownload download)
    {
        var name = download.SuggestedFilename;
        await download.CancelAsync().ConfigureAwait(false);
        if (_lastClick is not { } click)
        {
            return;
        }

        _lastClick = null;
        Emit(sequence => click with { Sequence = sequence, Kind = RecordedStepKind.Download, FileName = name, Replaces = click.Sequence, Time = DateTimeOffset.UtcNow });
    }

    /// <summary>The first candidate that matches exactly one element in the page (else the script's own count), and two more.</summary>
    private async Task<(string Selector, IReadOnlyList<string> Alternatives)> ChooseSelectorsAsync(IReadOnlyList<(string Selector, bool Unique)> candidates)
    {
        var chosen = new List<string>();
        foreach (var (text, unique) in candidates)
        {
            if (await IsUniqueAsync(text).ConfigureAwait(false) ?? unique)
            {
                chosen.Add(text);
                if (chosen.Count == 3)
                {
                    break;
                }
            }
        }

        // Nothing provably unique: the last candidate (an XPath to that element) is still the best description.
        return chosen.Count == 0 ? (candidates[^1].Selector, []) : (chosen[0], chosen.Skip(1).ToList());
    }

    /// <summary>True or false from the browser's count; null when the page is gone or changed (nothing matches any more).</summary>
    private async Task<bool?> IsUniqueAsync(string text)
    {
        if (_page is null || _page.IsClosed)
        {
            return null;
        }

        try
        {
            var count = await BrowserSelectors.ToLocator(_page, BrowserSelectors.Parse(text)).CountAsync().ConfigureAwait(false);
            return count == 0 ? null : count == 1;
        }
        catch (Exception ex) when (ex is PlaywrightException or AutomationException)
        {
            return null;
        }
    }

    private RecordedStep Emit(Func<int, RecordedStep> create)
    {
        RecordedStep step;
        lock (_gate)
        {
            step = create(++_sequence);
            if (step.Replaces is { } replaced && _steps.FindIndex(s => s.Sequence == replaced) is var at and >= 0)
            {
                _steps[at] = step;
            }
            else
            {
                _steps.Add(step);
            }
        }

        _listener.OnStep(step);
        return step;
    }

    private void End(RecordingEnd end)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
        {
            _work.Writer.TryComplete();
            _forget(this);
            _listener.OnEnded(end);
            _completion.TrySetResult();
        }
    }

    // IRecordingTestDriver: real browser input (trusted events), for automated tests only.
    private IPage Page => _page ?? throw new InvalidOperationException("The recording has no page.");

    private static ILocator Find(IPage page, string selector) => BrowserSelectors.ToLocator(page, BrowserSelectors.Parse(selector));

    /// <inheritdoc />
    public Task ClickAsync(string selector, CancellationToken cancellationToken) =>
        Find(Page, selector).ClickAsync(new LocatorClickOptions { Timeout = 5000 }).WaitAsync(cancellationToken);

    /// <inheritdoc />
    public Task FillAsync(string selector, string text, CancellationToken cancellationToken) =>
        Find(Page, selector).FillAsync(text, new LocatorFillOptions { Timeout = 5000 }).WaitAsync(cancellationToken);

    /// <inheritdoc />
    public Task PressAsync(string selector, string key, CancellationToken cancellationToken) =>
        Find(Page, selector).PressAsync(key, new LocatorPressOptions { Timeout = 5000 }).WaitAsync(cancellationToken);

    /// <inheritdoc />
    public Task GoToAsync(Uri url, CancellationToken cancellationToken) =>
        Page.GotoAsync(url.AbsoluteUri).WaitAsync(cancellationToken);

    /// <inheritdoc />
    public Task SelectAsync(string selector, IReadOnlyList<string> values, CancellationToken cancellationToken) =>
        Find(Page, selector).SelectOptionAsync(values, new LocatorSelectOptionOptions { Timeout = 5000 }).WaitAsync(cancellationToken);

    /// <inheritdoc />
    public Task SetFilesAsync(string selector, IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        Find(Page, selector).SetInputFilesAsync(paths, new LocatorSetInputFilesOptions { Timeout = 5000 }).WaitAsync(cancellationToken);
}
