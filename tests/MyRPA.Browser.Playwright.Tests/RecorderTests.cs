using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MyRPA.Browser.Contracts;

namespace MyRPA.Browser.Playwright.Tests;

/// <summary>
/// The browser recorder (ADR-0039) through the real plugin host: a recording session (headless here) driven with real
/// browser input; the recorded steps and their selectors (ADR-0038).
/// </summary>
[Collection(BrowserTestGroup.Name)]
public sealed class RecorderTests(BrowserHost host) : IClassFixture<BrowserHost>
{
    private static readonly CancellationToken _token = TestContext.Current.CancellationToken;

    private async Task<(IRecordingSession Session, IRecordingTestDriver User, Probe Probe)> StartAsync(string path = "")
    {
        var recorder = Assert.Single(host.Services.GetServices<IBrowserRecorder>());
        var probe = new Probe(); // disposed with the test class's process; it only holds a semaphore
        var session = await recorder.StartAsync(new RecordingOptions { StartUrl = new Uri(host.Site.BaseUrl + path), Headless = true }, probe, _token);
        return (session, Assert.IsAssignableFrom<IRecordingTestDriver>(session), probe);
    }

    [Fact]
    public async Task ClickTypeSelectUpload_AreRecordedWithSemanticSelectors()
    {
        var (session, user, probe) = await StartAsync();
        await using var _ = session;
        var file = Path.Combine(host.FileRoot, "invoice.pdf");
        await File.WriteAllTextAsync(file, "x", _token);

        await user.FillAsync("#name", "Ada", _token);
        await user.ClickAsync("#greet", _token);
        await user.SelectAsync("#color", ["g"], _token);
        await user.SetFilesAsync("#file", [file], _token);
        var steps = await probe.WaitForAsync(s => s.Count == 4);

        Assert.Equal([RecordedStepKind.Type, RecordedStepKind.Click, RecordedStepKind.Select, RecordedStepKind.Upload], steps.Select(s => s.Kind));
        Assert.Equal(("role=textbox|Name", "Ada"), (steps[0].Selector, steps[0].Text));
        Assert.Contains("label=Name", steps[0].Alternatives);
        Assert.Equal(("testid=greet-button", "button \"Greet\""), (steps[1].Selector, steps[1].Element));
        Assert.Equal(["g"], steps[2].Values);
        Assert.Equal(["invoice.pdf"], steps[3].Values);
        Assert.All(steps.Where(s => s.Kind != RecordedStepKind.Navigate), s => Assert.False(string.IsNullOrEmpty(s.Selector)));
    }

    [Fact]
    public async Task Typing_InOneField_ReplacesTheEarlierStep()
    {
        var (session, user, probe) = await StartAsync();
        await using var _ = session;

        await user.FillAsync("#name", "Ad", _token);
        await user.FillAsync("#name", "Ada Lovelace", _token);
        var steps = await probe.WaitForAsync(s => s.Count == 1 && s[0].Text == "Ada Lovelace");

        Assert.Equal(RecordedStepKind.Type, Assert.Single(steps).Kind);
        Assert.NotNull(probe.All[^1].Replaces);
    }

    [Fact]
    public async Task Password_IsNeverRecorded_AndEnterSubmitsByTheFormsButton()
    {
        var (session, user, probe) = await StartAsync("login");
        await using var _ = session;

        await user.FillAsync("label=Email", "ada@example.com", _token);
        await user.FillAsync("label=Password", "s3cret-value", _token);
        await user.PressAsync("label=Password", "Enter", _token);
        var steps = await probe.WaitForAsync(s => s.Count == 3);
        await Task.Delay(500, _token); // the navigation the submit caused must not become a step

        var password = steps[1];
        Assert.Equal((RecordedStepKind.Type, true, null), (password.Kind, password.Secret, password.Text));
        Assert.Equal((RecordedStepKind.Click, "role=button|Sign in"), (steps[2].Kind, steps[2].Selector));
        Assert.DoesNotContain(probe.All, s => (s.Text ?? string.Empty).Contains("s3cret", StringComparison.Ordinal));
        Assert.Equal(3, session.Steps.Count);
    }

    [Fact]
    public async Task Navigation_ByTheUser_IsRecorded_ButNotTheOneAClickCaused()
    {
        var (session, user, probe) = await StartAsync();
        await using var _ = session;

        await user.ClickAsync("#link", _token);
        await probe.WaitForAsync(s => s.Count == 1);
        await Task.Delay(2500, _token);
        await user.GoToAsync(new Uri(host.Site.BaseUrl + "login"), _token);
        var steps = await probe.WaitForAsync(s => s.Count == 2);

        Assert.Equal((RecordedStepKind.Click, "role=link|Other page"), (steps[0].Kind, steps[0].Selector));
        Assert.Equal((RecordedStepKind.Navigate, host.Site.BaseUrl + "login"), (steps[1].Kind, steps[1].Url));
    }

    [Fact]
    public async Task Download_ReplacesTheClickThatStartedIt()
    {
        var (session, user, probe) = await StartAsync();
        await using var _ = session;

        await user.ClickAsync("#download", _token);
        var steps = await probe.WaitForAsync(s => s.Count == 1 && s[0].Kind == RecordedStepKind.Download);

        Assert.Equal(("report.txt", "role=link|Download"), (steps[0].FileName, steps[0].Selector));
    }

    [Fact]
    public async Task Stop_EndsTheRecording()
    {
        var (session, _, probe) = await StartAsync();

        await session.StopAsync();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), _token);

        Assert.Equal(RecordingEndReason.Stopped, probe.Ended?.Reason);
    }

    [Theory]
    [InlineData("file:///c:/windows/win.ini")]
    [InlineData("about:blank")]
    public async Task StartUrl_MustBeHttpOrHttps(string url)
    {
        var recorder = Assert.Single(host.Services.GetServices<IBrowserRecorder>());

        await Assert.ThrowsAsync<ArgumentException>(async () => await recorder.StartAsync(new RecordingOptions { StartUrl = new Uri(url), Headless = true }, new Probe(), _token));
    }

    /// <summary>Collects the steps (keeping only the current ones, as a consumer does) and the end.</summary>
    private sealed class Probe : IRecordingListener, IDisposable
    {
        private readonly ConcurrentQueue<RecordedStep> _all = new();
        private readonly SemaphoreSlim _changed = new(0);

        public IReadOnlyList<RecordedStep> All => [.. _all];

        public RecordingEnd? Ended { get; private set; }

        public void Dispose() => _changed.Dispose();

        public void OnStep(RecordedStep recorded)
        {
            _all.Enqueue(recorded);
            _changed.Release();
        }

        public void OnEnded(RecordingEnd outcome)
        {
            Ended = outcome;
            _changed.Release();
        }

        public List<RecordedStep> Current()
        {
            var current = new List<RecordedStep>();
            foreach (var step in _all)
            {
                var at = step.Replaces is { } replaced ? current.FindIndex(s => s.Sequence == replaced) : -1;
                if (at >= 0)
                {
                    current[at] = step;
                }
                else
                {
                    current.Add(step);
                }
            }

            return current;
        }

        public async Task<IReadOnlyList<RecordedStep>> WaitForAsync(Func<IReadOnlyList<RecordedStep>, bool> done)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (!done(Current()))
            {
                try
                {
                    await _changed.WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!_token.IsCancellationRequested)
                {
                    Assert.Fail($"Timed out; steps: {string.Join(" | ", Current().Select(s => $"{s.Kind} {s.Selector} {s.Text} {s.Url}"))}");
                }
            }

            return Current();
        }
    }
}
