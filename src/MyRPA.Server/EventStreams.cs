using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MyRPA.Contracts.Execution;
using MyRPA.Execution.Hosting;

namespace MyRPA.Server;

/// <summary>
/// One event stream per browser tab, multiplexing many runs (ADR-0024). The SSE <c>id</c> of each event is the stream's
/// position vector (<c>0=12,1=5</c>: subscription index = last sequence delivered), so a reconnect with
/// <c>Last-Event-ID</c> resumes every run exactly where the client stopped. Runs are read through
/// <see cref="ExecutionHandle.ReadEventsAsync"/>, which provides replay and <c>stream.gap</c> reporting.
/// </summary>
internal sealed class EventStreams(ServerOptions options, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, EventStream> _streams = new(StringComparer.Ordinal);

    public EventStream? Create(string session)
    {
        Sweep();
        if (_streams.Count >= options.MaxStreams)
        {
            return null;
        }

        var stream = new EventStream(LocalSessions.NewSecret(), session, options, time);
        _streams[stream.Id] = stream;
        return stream;
    }

    /// <summary>Finds a stream of this session.</summary>
    public EventStream? Find(string id, string session) =>
        _streams.TryGetValue(id, out var stream) && stream.Session == session ? stream : null;

    public void Close(EventStream stream)
    {
        _streams.TryRemove(stream.Id, out _);
        stream.Disconnect();
    }

    private void Sweep()
    {
        foreach (var stream in _streams.Values.Where(s => s.IsExpired))
        {
            _streams.TryRemove(stream.Id, out _);
        }
    }
}

/// <summary>A tab's stream: its subscriptions (positions survive reconnects) and at most one live connection.</summary>
internal sealed class EventStream(string id, string session, ServerOptions options, TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private Connection? _connection;
    private DateTimeOffset _disconnectedAt = time.GetUtcNow();

    public string Id { get; } = id;

    public string Session { get; } = session;

    public bool IsExpired
    {
        get
        {
            lock (_gate)
            {
                return _connection is null && time.GetUtcNow() - _disconnectedAt > options.StreamRetention;
            }
        }
    }

    /// <summary>Subscribes a run; events after <paramref name="afterSequence"/> are delivered (now, or on the next connection).</summary>
    public SubscribeOutcome Subscribe(ExecutionHandle run, long afterSequence)
    {
        lock (_gate)
        {
            if (_subscriptions.Any(s => s.Run.RunId == run.RunId && !s.Removed))
            {
                return SubscribeOutcome.AlreadySubscribed;
            }

            if (_subscriptions.Count(s => !s.Removed) >= options.MaxSubscriptionsPerStream)
            {
                return SubscribeOutcome.LimitReached;
            }

            var subscription = new Subscription(_subscriptions.Count, run) { Position = afterSequence };
            _subscriptions.Add(subscription);
            _connection?.Start(subscription);
            return SubscribeOutcome.Subscribed;
        }
    }

    public bool Unsubscribe(string runId)
    {
        lock (_gate)
        {
            var subscription = _subscriptions.FirstOrDefault(s => s.Run.RunId == runId && !s.Removed);
            if (subscription is null)
            {
                return false;
            }

            subscription.Removed = true;
            subscription.Pump?.Cancel();
            return true;
        }
    }

    /// <summary>Serves the stream on this response until the client disconnects (replacing any previous connection).</summary>
    public async Task ServeAsync(HttpResponse response, string? lastEventId, CancellationToken aborted)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        var connection = new Connection(options.StreamQueueCapacity, lifetime);
        lock (_gate)
        {
            _connection?.Lifetime.Cancel();
            ApplyCursor(lastEventId);
            _connection = connection;
            foreach (var subscription in _subscriptions.Where(s => !s.Removed))
            {
                connection.Start(subscription);
            }
        }

        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no";
        try
        {
            await WriteAsync(response, $"retry: 2000\nevent: stream.opened\ndata: {{\"streamId\":\"{Id}\"}}\n\n", lifetime.Token).ConfigureAwait(false);
            while (true)
            {
                using var keepAlive = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                keepAlive.CancelAfter(options.KeepAliveInterval);
                (Subscription Subscription, ExecutionEventMessage Message) item;
                try
                {
                    item = await connection.Queue.Reader.ReadAsync(keepAlive.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                {
                    await WriteAsync(response, ": keep-alive\n\n", lifetime.Token).ConfigureAwait(false);
                    continue;
                }

                string cursor;
                lock (_gate)
                {
                    if (item.Subscription.Removed)
                    {
                        continue;
                    }

                    if (item.Message.Sequence > 0)
                    {
                        item.Subscription.Position = item.Message.Sequence;
                    }

                    cursor = string.Join(',', _subscriptions.Where(s => !s.Removed).Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Index}={s.Position}")));
                }

                var data = JsonSerializer.Serialize(item.Message, ContractsJsonContext.Default.ExecutionEventMessage);
                await WriteAsync(response, $"id: {cursor}\nevent: {item.Message.Kind}\ndata: {data}\n\n", lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The client left, a newer connection replaced this one, or the queue overflowed: the client resumes by cursor.
        }
        finally
        {
            lock (_gate)
            {
                if (_connection == connection)
                {
                    _connection = null;
                    _disconnectedAt = time.GetUtcNow();
                }
            }

            await connection.StopAsync().ConfigureAwait(false);
        }
    }

    public void Disconnect()
    {
        lock (_gate)
        {
            _connection?.Lifetime.Cancel();
        }
    }

    // Last-Event-ID is "index=sequence,…": the client's positions win over what the server last sent.
    private void ApplyCursor(string? lastEventId)
    {
        if (string.IsNullOrWhiteSpace(lastEventId))
        {
            return;
        }

        foreach (var part in lastEventId.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=');
            if (pair.Length == 2 && int.TryParse(pair[0], CultureInfo.InvariantCulture, out var index) && long.TryParse(pair[1], CultureInfo.InvariantCulture, out var position)
                && index >= 0 && index < _subscriptions.Count && position >= 0)
            {
                _subscriptions[index].Position = position;
            }
        }
    }

    private static async Task WriteAsync(HttpResponse response, string text, CancellationToken cancellationToken)
    {
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal sealed class Subscription(int index, ExecutionHandle run)
    {
        public int Index { get; } = index;

        public ExecutionHandle Run { get; } = run;

        public long Position { get; set; }

        public bool Removed { get; set; }

        public CancellationTokenSource? Pump { get; set; }
    }

    /// <summary>One live connection: a bounded queue fed by one pump per subscription.</summary>
    private sealed class Connection(int capacity, CancellationTokenSource lifetime)
    {
        private readonly List<Task> _pumps = [];

        public Channel<(Subscription, ExecutionEventMessage)> Queue { get; } =
            Channel.CreateBounded<(Subscription, ExecutionEventMessage)>(new BoundedChannelOptions(capacity) { SingleReader = true });

        public CancellationTokenSource Lifetime { get; } = lifetime;

        public void Start(Subscription subscription)
        {
            var pump = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
            subscription.Pump = pump;
            _pumps.Add(Task.Run(() => PumpAsync(subscription, subscription.Position, pump.Token)));
        }

        public async Task StopAsync()
        {
            await Lifetime.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(_pumps).ConfigureAwait(false);
        }

        private async Task PumpAsync(Subscription subscription, long after, CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var message in subscription.Run.ReadEventsAsync(after, cancellationToken).ConfigureAwait(false))
                {
                    if (!Queue.Writer.TryWrite((subscription, message)))
                    {
                        // The client is too slow: drop the connection rather than buffer without bound (ADR-0024).
                        await Lifetime.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Unsubscribed or disconnected.
            }
        }
    }
}

internal enum SubscribeOutcome
{
    Subscribed,
    AlreadySubscribed,
    LimitReached,
}
