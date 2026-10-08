using System.Runtime.CompilerServices;
using System.Text.Json;
using MyRPA.Browser.Contracts;
using MyRPA.Contracts.Execution;
using MyRPA.Contracts.Recording;

namespace MyRPA.Server;

/// <summary>
/// Recording sessions started from the Studio (ADR-0039): one at a time per server, owned by the session that started it,
/// using the browser plugin's recorder (an extension contract). Steps go to the tabs' event streams.
/// </summary>
internal sealed class Recordings(IEnumerable<IBrowserRecorder> recorders, ServerOptions options, TimeProvider time) : IAsyncDisposable
{
    private readonly IBrowserRecorder? _recorder = recorders.FirstOrDefault();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RecordingHandle> _all = new(StringComparer.Ordinal);
    private RecordingHandle? _active;

    /// <summary>Why recording is not possible on this server (no browser plugin), or null.</summary>
    public string? Unavailable => _recorder is null ? "Recording needs the browser plugin (MyRPA.Browser.Playwright): start the server with it." : null;

    /// <summary>Starts a recording at <paramref name="startUrl"/>, or returns the reason it cannot start and an HTTP status.</summary>
    public async Task<(RecordingHandle? Handle, int Status, string? Error)> StartAsync(string session, Uri startUrl, CancellationToken cancellationToken)
    {
        if (_recorder is null)
        {
            return (null, StatusCodes.Status503ServiceUnavailable, Unavailable);
        }

        var handle = new RecordingHandle(LocalSessions.NewSecret(), session, startUrl, time, Forget);
        lock (_gate)
        {
            if (_active is not null)
            {
                return (null, StatusCodes.Status409Conflict, "A recording is already running; stop it first.");
            }

            _active = handle;
        }

        try
        {
            handle.Attach(await _recorder.StartAsync(new RecordingOptions { StartUrl = startUrl, Headless = options.RecorderHeadless }, handle, cancellationToken).ConfigureAwait(false));
            lock (_gate)
            {
                _all[handle.Id] = handle;
            }

            return (handle, StatusCodes.Status201Created, null);
        }
        catch (ArgumentException ex)
        {
            Forget(handle);
            return (null, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Forget(handle);
            return (null, StatusCodes.Status500InternalServerError, $"The recording browser could not start: {ex.Message}");
        }
    }

    /// <summary>The running recording, if any (one at a time per server).</summary>
    public RecordingHandle? Active
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    /// <summary>A recording of this session.</summary>
    public RecordingHandle? Find(string id, string session)
    {
        lock (_gate)
        {
            return _all.TryGetValue(id, out var handle) && handle.Session == session ? handle : null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        RecordingHandle? active;
        lock (_gate)
        {
            active = _active;
        }

        if (active is not null)
        {
            await active.StopAsync().ConfigureAwait(false);
        }
    }

    private void Forget(RecordingHandle handle)
    {
        lock (_gate)
        {
            if (_active == handle)
            {
                _active = null;
            }
        }
    }
}

/// <summary>One recording: receives its steps (any thread) and serves them to event streams in order.</summary>
internal sealed class RecordingHandle(string id, string session, Uri startUrl, TimeProvider time, Action<RecordingHandle> ended) : IRecordingListener, IStreamSource
{
    private readonly Lock _gate = new();
    private readonly List<StreamEvent> _events = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IRecordingSession? _recording;
    private RecordingEnd? _end;

    public string Id { get; } = id;

    public string Session { get; } = session;

    public Uri StartUrl { get; } = startUrl;

    /// <summary>The recorder's session (for the test driver in server tests); null until started.</summary>
    public IRecordingSession? Recording => _recording;

    public RecordingEnd? End
    {
        get
        {
            lock (_gate)
            {
                return _end;
            }
        }
    }

    public void Attach(IRecordingSession recording) => _recording = recording;

    public async Task StopAsync()
    {
        if (_recording is { } recording && End is null)
        {
            await recording.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The current steps, for <c>GET /api/recordings/{id}</c>.</summary>
    public IReadOnlyList<RecordedStepMessage> Steps => [.. (_recording?.Steps ?? []).Select(ToMessage)];

    public void OnStep(RecordedStep recorded) => Append(new RecordingEventMessage
    {
        Sequence = 0,
        Kind = RecordingEventKinds.Step,
        RecordingId = Id,
        Time = time.GetUtcNow(),
        Step = ToMessage(recorded),
    });

    public void OnEnded(RecordingEnd outcome)
    {
        lock (_gate)
        {
            _end = outcome;
        }

        Append(new RecordingEventMessage
        {
            Sequence = 0,
            Kind = RecordingEventKinds.Ended,
            RecordingId = Id,
            Time = time.GetUtcNow(),
            EndReason = outcome.Reason.ToString(),
            Message = outcome.Message,
        });
        ended(this);
    }

    public async IAsyncEnumerable<StreamEvent> ReadAsync(long after, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var position = after;
        while (true)
        {
            StreamEvent[] pending;
            Task changed;
            bool over;
            lock (_gate)
            {
                pending = [.. _events.Where(e => e.Sequence > position)];
                changed = _changed.Task;
                over = _end is not null && _events.Count > 0 && _events[^1].Kind == RecordingEventKinds.Ended;
            }

            foreach (var item in pending)
            {
                position = item.Sequence;
                yield return item;
            }

            if (pending.Length == 0)
            {
                if (over)
                {
                    yield break;
                }

                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static RecordedStepMessage ToMessage(RecordedStep step) => new()
    {
        Sequence = step.Sequence,
        Kind = step.Kind.ToString().ToLowerInvariant(),
        Selector = step.Selector,
        Alternatives = step.Alternatives,
        Element = step.Element,
        Text = step.Text,
        Secret = step.Secret,
        Values = step.Values,
        Url = step.Url,
        FileName = step.FileName,
        Replaces = step.Replaces,
    };

    private void Append(RecordingEventMessage message)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            var sequence = _events.Count + 1;
            var data = JsonSerializer.Serialize(message with { Sequence = sequence }, ContractsJsonContext.Default.RecordingEventMessage);
            _events.Add(new StreamEvent(sequence, message.Kind, data));
            changed = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        changed.TrySetResult();
    }
}
