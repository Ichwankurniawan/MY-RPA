using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MyRPA.Browser.Contracts;

namespace MyRPA.Server.Tests;

/// <summary>
/// Recordings through the server (ADR-0039): start, steps on the tab's event stream, stop; one at a time; session-bound;
/// unavailable without the browser plugin. The recorder runs headless here (<c>--recorder-headless</c>) and is driven
/// with real browser input through the session's test driver (in-process only).
/// </summary>
public sealed class RecordingTests
{
    private static string BrowserPlugin { get; } = Path.GetFullPath(
        typeof(RecordingTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "BrowserPluginDirectory").Value!);

    private static Task<ServerHarness> StartAsync() =>
        ServerHarness.StartAsync(o => new ServerOptions { Projects = o.Projects, Port = 0, PluginDirectories = [BrowserPlugin], RecorderHeadless = true });

    private static async Task<string> StartRecordingAsync(ServerHarness h, string url)
    {
        using var response = await h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/recordings", new { startUrl = url }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ServerHarness.JsonAsync(response)).GetProperty("recordingId").GetString()!;
    }

    private static IRecordingTestDriver User(ServerHarness h, string recordingId)
    {
        var handle = h.Server.App.Services.GetRequiredService<Recordings>().Active;
        Assert.Equal(recordingId, handle?.Id);
        return Assert.IsAssignableFrom<IRecordingTestDriver>(handle!.Recording);
    }

    [Fact]
    public async Task Recording_SendsStepsOnTheEventStream_AndStops()
    {
        using var site = new Page();
        await using var h = await StartAsync();
        var streamId = await h.OpenStreamAsync();
        await using var sse = await SseReader.OpenAsync(h.Client, streamId);
        var id = await StartRecordingAsync(h, site.Url);
        using (var subscribe = await h.SendAsync(h.Unsafe(HttpMethod.Post, $"/api/streams/{streamId}/subscriptions", new { recordingId = id })))
        {
            Assert.Equal(HttpStatusCode.Created, subscribe.StatusCode);
        }

        var user = User(h, id);
        await user.FillAsync("label=Name", "Ada", TestContext.Current.CancellationToken);
        await user.ClickAsync("testid=go", TestContext.Current.CancellationToken);
        var steps = await sse.ReadUntilAsync(events => events.Count(e => e.Kind == "recording.step") >= 2);
        using (var stop = await h.SendAsync(h.Unsafe(HttpMethod.Delete, $"/api/recordings/{id}")))
        {
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        }

        var all = steps.Concat(await sse.ReadUntilAsync(events => events.Any(e => e.Kind == "recording.ended"))).ToList();
        var recorded = all.Where(e => e.Kind == "recording.step").Select(e => e.Data.GetProperty("step")).ToList();
        Assert.Equal(("type", "Ada"), (recorded[0].GetProperty("kind").GetString(), recorded[0].GetProperty("text").GetString()));
        Assert.Equal(("click", "testid=go"), (recorded[^1].GetProperty("kind").GetString(), recorded[^1].GetProperty("selector").GetString()));
        Assert.All(all.Where(e => e.Kind.StartsWith("recording.", StringComparison.Ordinal)), e => Assert.Equal(id, e.Data.GetProperty("recordingId").GetString()));
        Assert.Equal("Stopped", all.Single(e => e.Kind == "recording.ended").Data.GetProperty("endReason").GetString());

        using var get = await h.Client.GetAsync($"/api/recordings/{id}", TestContext.Current.CancellationToken);
        var json = await ServerHarness.JsonAsync(get);
        Assert.False(json.GetProperty("active").GetBoolean());
        Assert.Equal(["type", "click"], json.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task OneRecordingAtATime_AndAnotherCanStartAfterStop()
    {
        using var site = new Page();
        await using var h = await StartAsync();
        var first = await StartRecordingAsync(h, site.Url);

        using (var second = await h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/recordings", new { startUrl = site.Url })))
        {
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        }

        using (var stop = await h.SendAsync(h.Unsafe(HttpMethod.Delete, $"/api/recordings/{first}")))
        {
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        }

        var again = await StartRecordingAsync(h, site.Url);
        using var stopAgain = await h.SendAsync(h.Unsafe(HttpMethod.Delete, $"/api/recordings/{again}"));
        Assert.Equal(HttpStatusCode.NoContent, stopAgain.StatusCode);
    }

    [Theory]
    [InlineData("file:///c:/windows/win.ini")]
    [InlineData("not a url")]
    public async Task StartUrl_MustBeHttpOrHttps(string url)
    {
        await using var h = await StartAsync();

        using var response = await h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/recordings", new { startUrl = url }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WithoutTheBrowserPlugin_RecordingIsUnavailable()
    {
        await using var h = await ServerHarness.StartAsync();

        using var info = await h.Client.GetAsync("/api/recordings", TestContext.Current.CancellationToken);
        var json = await ServerHarness.JsonAsync(info);
        using var start = await h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/recordings", new { startUrl = "http://127.0.0.1/" }));

        Assert.False(json.GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
        Assert.Contains("browser plugin", (await ServerHarness.JsonAsync(start)).GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recordings_NeedTheSession_AndTheAntiForgeryHeader()
    {
        await using var h = await StartAsync();
        using var anonymous = ServerHarness.NewClient(h.BaseUri);

        using var unsigned = await anonymous.GetAsync("/api/recordings/x", TestContext.Current.CancellationToken);
        using var forged = await h.Client.PostAsync("/api/recordings", new StringContent("""{ "startUrl": "http://127.0.0.1/" }""", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        using var unknown = await h.Client.GetAsync("/api/recordings/unknown", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        Assert.NotEqual(HttpStatusCode.Created, forged.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    /// <summary>A tiny local page to record on (loopback only).</summary>
    private sealed class Page : IDisposable
    {
        private readonly HttpListener _listener = new();

        public Page()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://localhost:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string Url { get; }

        public void Dispose() => _listener.Close();

        private async Task ServeAsync()
        {
            const string html = """
                <!doctype html><html><head><title>Form</title></head><body>
                  <label for="name">Name</label><input id="name" />
                  <button data-testid="go">Go</button>
                </body></html>
                """;
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                var bytes = Encoding.UTF8.GetBytes(html);
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }
    }
}
