namespace MyRPA.Contracts.Recording;

/// <summary>Kinds of recording events on a tab's event stream (ADR-0039).</summary>
public static class RecordingEventKinds
{
    /// <summary>A recorded step (new, or replacing an earlier one).</summary>
    public const string Step = "recording.step";

    /// <summary>The recording ended.</summary>
    public const string Ended = "recording.ended";
}

/// <summary>One recording event, as sent on the event stream (SSE <c>data</c>).</summary>
public sealed record RecordingEventMessage
{
    /// <summary>1, 2, 3… per recording (the stream's resume position).</summary>
    public required long Sequence { get; init; }

    /// <summary><see cref="RecordingEventKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The recording.</summary>
    public required string RecordingId { get; init; }

    /// <summary>When the server received it.</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>The step (<see cref="RecordingEventKinds.Step"/>).</summary>
    public RecordedStepMessage? Step { get; init; }

    /// <summary>Why it ended (<see cref="RecordingEventKinds.Ended"/>): <c>Stopped</c>, <c>BrowserClosed</c> or <c>Failed</c>.</summary>
    public string? EndReason { get; init; }

    /// <summary>Details of a failure.</summary>
    public string? Message { get; init; }
}

/// <summary>A recorded step on the wire (the browser contracts' <c>RecordedStep</c>).</summary>
public sealed record RecordedStepMessage
{
    /// <summary>The step's sequence in its recording.</summary>
    public required int Sequence { get; init; }

    /// <summary><c>navigate</c>, <c>click</c>, <c>type</c>, <c>select</c>, <c>upload</c> or <c>download</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The element's selector (ADR-0038).</summary>
    public string? Selector { get; init; }

    /// <summary>Other selectors for the same element.</summary>
    public IReadOnlyList<string> Alternatives { get; init; } = [];

    /// <summary>A short description of the element.</summary>
    public string? Element { get; init; }

    /// <summary>Typed text (never a password's).</summary>
    public string? Text { get; init; }

    /// <summary>A password was typed; its text was not recorded.</summary>
    public bool Secret { get; init; }

    /// <summary>Selected option values or file names.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>A navigation's URL.</summary>
    public string? Url { get; init; }

    /// <summary>A download's suggested file name.</summary>
    public string? FileName { get; init; }

    /// <summary>The sequence of the step this one replaces.</summary>
    public int? Replaces { get; init; }
}
