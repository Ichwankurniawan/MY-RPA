using System.Globalization;
using MyRPA.Workflow.Execution;

namespace MyRPA.Browser.Playwright;

/// <summary>
/// The browser sessions of one workflow run (run lifetime, ADR-0017): created with the run's services, shared with the
/// workflows it invokes, and disposed when the run ends in any status. Disposal closes every session that is still
/// open, so no browser outlives the run that opened it.
/// </summary>
/// <param name="provider">The browser provider.</param>
public sealed class BrowserSessions(IBrowserProvider provider) : IAsyncDisposable
{
    /// <summary>Upper bound for closing one session during clean-up.</summary>
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, IBrowserSession> _open = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private int _next;
    private bool _disposed;

    /// <summary>The ids of the sessions that are open.</summary>
    public IReadOnlyList<string> OpenSessionIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _open.Keys.Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>Launches a browser and registers the session.</summary>
    /// <param name="options">Browser options.</param>
    /// <param name="limits">Launch timeout and cancellation.</param>
    public async ValueTask<IBrowserSession> OpenAsync(BrowserLaunchOptions options, OperationLimits limits)
    {
        var id = "browser-" + Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = await provider.LaunchAsync(id, options, limits).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_disposed)
            {
                _open.Add(id, session);
                return session;
            }
        }

        // The run ended while the browser was starting.
        await session.CloseAsync().ConfigureAwait(false);
        throw new ObjectDisposedException(nameof(BrowserSessions));
    }

    /// <summary>
    /// Returns the open session with <paramref name="id"/>, or — when <paramref name="id"/> is null — the only open session.
    /// </summary>
    /// <param name="id">Session id from <c>Browser.Open</c>, or null.</param>
    /// <exception cref="ActivityFailedException"><c>SessionNotFound</c> or <c>SessionClosed</c>.</exception>
    public IBrowserSession Get(string? id)
    {
        lock (_gate)
        {
            IBrowserSession session;
            if (id is null)
            {
                session = _open.Count switch
                {
                    1 => _open.Values.Single(),
                    0 => throw new ActivityFailedException(BrowserErrorTypes.SessionNotFound, "No browser session is open; use Browser.Open first."),
                    _ => throw new ActivityFailedException(BrowserErrorTypes.SessionNotFound, $"{_open.Count} browser sessions are open; set 'session' to choose one."),
                };
            }
            else if (!_open.TryGetValue(id, out session!))
            {
                throw new ActivityFailedException(BrowserErrorTypes.SessionNotFound, $"No open browser session has id '{id}'.");
            }

            return session.IsClosed ? throw BrowserErrors.Closed() : session;
        }
    }

    /// <summary>Closes and forgets a session.</summary>
    /// <param name="id">Session id, or null for the only open session.</param>
    public async ValueTask CloseAsync(string? id)
    {
        IBrowserSession session;
        lock (_gate)
        {
            session = id is null ? GetOnlyUnchecked() : _open.TryGetValue(id, out var found) ? found
                : throw new ActivityFailedException(BrowserErrorTypes.SessionNotFound, $"No open browser session has id '{id}'.");
            _open.Remove(session.Info.Id);
        }

        await session.CloseAsync().ConfigureAwait(false);
    }

    /// <summary>Closes every open session (bounded per session). Called when the run ends.</summary>
    public async ValueTask DisposeAsync()
    {
        IBrowserSession[] sessions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            sessions = [.. _open.Values];
            _open.Clear();
        }

        foreach (var session in sessions)
        {
            try
            {
                await session.CloseAsync().AsTask().WaitAsync(CloseTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The browser did not close in time; the provider's driver shutdown (host exit) ends it.
            }
        }
    }

    private IBrowserSession GetOnlyUnchecked() => _open.Count switch
    {
        1 => _open.Values.Single(),
        0 => throw new ActivityFailedException(BrowserErrorTypes.SessionNotFound, "No browser session is open."),
        _ => throw new ActivityFailedException(BrowserErrorTypes.SessionNotFound, $"{_open.Count} browser sessions are open; set 'session' to choose one."),
    };
}
