namespace MyRPA.Browser.Contracts;

/// <summary>
/// Records what a person does in a real browser (ADR-0039), as suggested steps with semantic selectors (ADR-0038).
/// Implemented by a browser plugin; used by the server. A recording never runs a workflow.
/// </summary>
public interface IBrowserRecorder
{
    /// <summary>Opens a browser at <see cref="RecordingOptions.StartUrl"/> and records until stopped or closed.</summary>
    /// <param name="options">Where to start.</param>
    /// <param name="listener">Receives the steps and the end, on a background thread, in order.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <exception cref="ArgumentException">The start URL is not an absolute http or https URL.</exception>
    ValueTask<IRecordingSession> StartAsync(RecordingOptions options, IRecordingListener listener, CancellationToken cancellationToken);

    /// <summary>
    /// Turns reviewed steps into workflow nodes of the plugin's own activities (the plugin knows them; the Studio does
    /// not): JSON <c>{ "nodes": [v1.0 nodes], "arguments": [argument definitions the nodes need] }</c>. The nodes open
    /// the browser at <paramref name="startUrl"/>, perform the steps and close it; a password becomes an In argument.
    /// </summary>
    /// <param name="startUrl">The recording's start URL.</param>
    /// <param name="steps">The steps, in order, as the user kept and edited them.</param>
    /// <exception cref="ArgumentException">A step is not valid (for example a selector that does not parse).</exception>
    string GenerateActivities(Uri startUrl, IReadOnlyList<RecordedStep> steps);
}

/// <summary>How a recording starts.</summary>
public sealed class RecordingOptions
{
    /// <summary>The first page (absolute http or https).</summary>
    public required Uri StartUrl { get; init; }

    /// <summary>For automated tests only: no visible window. A person records in a visible browser.</summary>
    public bool Headless { get; init; }

    /// <summary>
    /// For automated end-to-end tests only: a local DevTools port, so a test can act as the user in the recording
    /// browser. Never set for a person's recording.
    /// </summary>
    public int? DebuggingPort { get; init; }
}

/// <summary>Receives a recording's steps (each may replace an earlier one) and its end.</summary>
public interface IRecordingListener
{
    /// <summary>A new step, or a replacement of the step <see cref="RecordedStep.Replaces"/>.</summary>
    /// <param name="recorded">The step.</param>
    void OnStep(RecordedStep recorded);

    /// <summary>The recording ended (called once).</summary>
    /// <param name="outcome">Why.</param>
    void OnEnded(RecordingEnd outcome);
}

/// <summary>A running recording.</summary>
public interface IRecordingSession : IAsyncDisposable
{
    /// <summary>The recording's id.</summary>
    string Id { get; }

    /// <summary>The first page.</summary>
    Uri StartUrl { get; }

    /// <summary>The current steps, in order (replaced steps are not in the list).</summary>
    IReadOnlyList<RecordedStep> Steps { get; }

    /// <summary>Completes when the recording has ended.</summary>
    Task Completion { get; }

    /// <summary>Stops recording and closes the browser.</summary>
    ValueTask StopAsync();
}

/// <summary>What the user did.</summary>
public enum RecordedStepKind
{
    /// <summary>Went to a URL (not by a recorded click).</summary>
    Navigate = 0,

    /// <summary>Clicked an element.</summary>
    Click = 1,

    /// <summary>Typed into a field (the text so far; a password's text is never recorded).</summary>
    Type = 2,

    /// <summary>Chose options in a select.</summary>
    Select = 3,

    /// <summary>Chose files for a file input (names only).</summary>
    Upload = 4,

    /// <summary>A click that started a download.</summary>
    Download = 5,
}

/// <summary>One recorded step: a suggestion the user reviews before it becomes an activity.</summary>
/// <param name="Sequence">1, 2, 3… in the order steps were reported (replacements get new numbers).</param>
/// <param name="Kind">What the user did.</param>
/// <param name="Time">When.</param>
public sealed record RecordedStep(int Sequence, RecordedStepKind Kind, DateTimeOffset Time)
{
    /// <summary>The element, as selector text (ADR-0038), for every kind but <see cref="RecordedStepKind.Navigate"/>.</summary>
    public string? Selector { get; init; }

    /// <summary>Other selectors that also match exactly that element (at most two).</summary>
    public IReadOnlyList<string> Alternatives { get; init; } = [];

    /// <summary>A short description of the element, such as <c>button "Sign in"</c>.</summary>
    public string? Element { get; init; }

    /// <summary>The typed text (<see cref="RecordedStepKind.Type"/>); null for a password.</summary>
    public string? Text { get; init; }

    /// <summary>The field is a password: the text was not recorded.</summary>
    public bool Secret { get; init; }

    /// <summary>Selected option values (<see cref="RecordedStepKind.Select"/>) or file names (<see cref="RecordedStepKind.Upload"/>).</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>The URL (<see cref="RecordedStepKind.Navigate"/>).</summary>
    public string? Url { get; init; }

    /// <summary>The suggested file name (<see cref="RecordedStepKind.Download"/>).</summary>
    public string? FileName { get; init; }

    /// <summary>The sequence of the step this one replaces (more typing in the same field; a click that downloaded).</summary>
    public int? Replaces { get; init; }
}

/// <summary>Why a recording ended.</summary>
public enum RecordingEndReason
{
    /// <summary>Stopped by <see cref="IRecordingSession.StopAsync"/>.</summary>
    Stopped = 0,

    /// <summary>The user closed the browser.</summary>
    BrowserClosed = 1,

    /// <summary>The browser or the recorder failed.</summary>
    Failed = 2,
}

/// <summary>The end of a recording.</summary>
/// <param name="Reason">Why.</param>
/// <param name="Message">Details for <see cref="RecordingEndReason.Failed"/>.</param>
public sealed record RecordingEnd(RecordingEndReason Reason, string? Message = null);

/// <summary>
/// Acts as the user in a recording session, with real browser input (for automated tests of the recorder and the
/// Studio). Implemented by the recording session; never exposed by the server.
/// </summary>
public interface IRecordingTestDriver
{
    /// <summary>Clicks the element.</summary>
    /// <param name="selector">Selector text (ADR-0038).</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    Task ClickAsync(string selector, CancellationToken cancellationToken);

    /// <summary>Types the text into the field (replacing its content).</summary>
    /// <param name="selector">Selector text.</param>
    /// <param name="text">The text.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    Task FillAsync(string selector, string text, CancellationToken cancellationToken);

    /// <summary>Presses a key (for example <c>Enter</c>) in the element.</summary>
    /// <param name="selector">Selector text.</param>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    Task PressAsync(string selector, string key, CancellationToken cancellationToken);

    /// <summary>Goes to a URL, as typed in the address bar.</summary>
    /// <param name="url">The URL.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    Task GoToAsync(Uri url, CancellationToken cancellationToken);

    /// <summary>Selects options by value.</summary>
    /// <param name="selector">Selector text.</param>
    /// <param name="values">Option values.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    Task SelectAsync(string selector, IReadOnlyList<string> values, CancellationToken cancellationToken);

    /// <summary>Chooses files for a file input.</summary>
    /// <param name="selector">Selector text.</param>
    /// <param name="paths">Local file paths.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    Task SetFilesAsync(string selector, IReadOnlyList<string> paths, CancellationToken cancellationToken);
}
