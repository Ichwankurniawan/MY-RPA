using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace MyRPA.Server.Tests;

/// <summary>A real server on a random loopback port with a temporary project, and a browser-like client.</summary>
internal sealed class ServerHarness : IAsyncDisposable
{
    private readonly RunningServer _server;

    private ServerHarness(RunningServer server, string projectRoot)
    {
        _server = server;
        ProjectRoot = projectRoot;
        Client = NewClient(server.BaseUri);
    }

    public string ProjectRoot { get; }

    public string ProjectName => Path.GetFileName(ProjectRoot);

    public Uri BaseUri => _server.BaseUri;

    public Uri? StartUri => _server.StartUri;

    /// <summary>A client with a cookie jar, not yet signed in.</summary>
    public HttpClient Client { get; }

    internal RunningServer Server => _server;

    public static async Task<ServerHarness> StartAsync(Func<ServerOptions, ServerOptions>? configure = null, bool signIn = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "myrpa-server-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        var options = new ServerOptions { Projects = [new ProjectRoot(Path.GetFileName(root), root)], Port = 0 };
        options = configure?.Invoke(options) ?? options;
        var plugins = await ServerApplication.LoadPluginsAsync(options, TestContext.Current.CancellationToken);
        var server = await ServerApplication.StartAsync(options, plugins, TestContext.Current.CancellationToken);
        var harness = new ServerHarness(server, root);
        if (signIn)
        {
            await harness.SignInAsync(harness.Client);
        }

        return harness;
    }

    public static HttpClient NewClient(Uri baseUri) =>
        new(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true, AllowAutoRedirect = false }) { BaseAddress = baseUri };

    public async Task SignInAsync(HttpClient client)
    {
        var start = StartUri ?? throw new InvalidOperationException("The start link was already used.");
        using var response = await client.GetAsync(start.PathAndQuery, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    public string WriteWorkflow(string relativePath, string json)
    {
        var full = Path.Combine(ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, json);
        return full;
    }

    /// <summary>A state-changing request as the Web Studio sends it: same Origin, anti-forgery header, JSON body.</summary>
    public HttpRequestMessage Unsafe(HttpMethod method, string path, object? body = null, string? rawJson = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", BaseUri.GetLeftPart(UriPartial.Authority));
        request.Headers.Add("X-MyRPA-Request", "1");
        if (rawJson is not null)
        {
            request.Content = new StringContent(rawJson, Encoding.UTF8, "application/json");
        }
        else if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => await Client.SendAsync(request, TestContext.Current.CancellationToken);

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task<string> StartRunAsync(string path, object? document = null, object? arguments = null, int? timeoutMs = null)
    {
        using var response = await SendAsync(Unsafe(HttpMethod.Post, "/api/runs", new { project = ProjectName, path, document, arguments, timeoutMs }));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("runId").GetString()!;
    }

    public async Task<string> OpenStreamAsync()
    {
        using var response = await SendAsync(Unsafe(HttpMethod.Post, "/api/streams"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("streamId").GetString()!;
    }

    public async Task SubscribeAsync(string streamId, string runId, long afterSequence = 0)
    {
        using var response = await SendAsync(Unsafe(HttpMethod.Post, $"/api/streams/{streamId}/subscriptions", new { runId, afterSequence }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    public async Task<JsonElement> WaitForRunAsync(string runId)
    {
        for (var i = 0; i < 400; i++)
        {
            using var response = await Client.GetAsync($"/api/runs/{runId}", TestContext.Current.CancellationToken);
            var json = await JsonAsync(response);
            if (json.TryGetProperty("result", out var result))
            {
                return result;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Run {runId} did not finish.");
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _server.DisposeAsync();
        try
        {
            Directory.Delete(ProjectRoot, recursive: true);
        }
        catch (IOException)
        {
            // A file may still be held briefly; the temp folder is cleaned by the OS eventually.
        }
    }

    public static string Workflow(string root, string arguments = "[]", string id = "test") =>
        $$"""{ "schemaVersion": "1.0", "id": "{{id}}", "name": "Test", "version": "1.0.0", "arguments": {{arguments}}, "root": {{root}} }""";

    public static string Logs(int count, string id = "logs") => Workflow(
        $$"""{ "id": "main", "type": "Core.Sequence", "children": [ {{string.Join(", ", Enumerable.Range(0, count).Select(i => $$"""{ "id": "l{{i}}", "type": "Core.Log", "properties": { "message": "'line {{i}}'" } }"""))}} ] }""",
        id: id);
}

/// <summary>Reads server-sent events from a response.</summary>
internal sealed class SseReader : IAsyncDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly StreamReader _reader;

    private SseReader(HttpResponseMessage response, Stream stream)
    {
        _response = response;
        _reader = new StreamReader(stream);
    }

    public static async Task<SseReader> OpenAsync(HttpClient client, string streamId, string? lastEventId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/streams/{streamId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (lastEventId is not null)
        {
            request.Headers.Add("Last-Event-ID", lastEventId);
        }

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        return new SseReader(response, await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>The next event (comments such as keep-alives are skipped).</summary>
    public async Task<SseEvent> NextAsync()
    {
        string? id = null, kind = null, data = null;
        while (true)
        {
            var line = await _reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)
                ?? throw new EndOfStreamException("The stream closed.");
            if (line.Length == 0)
            {
                if (kind is not null || data is not null)
                {
                    return new SseEvent(id, kind ?? "message", data is null ? default : JsonDocument.Parse(data).RootElement.Clone());
                }

                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..].TrimStart();
            switch (field)
            {
                case "id":
                    id = value;
                    break;
                case "event":
                    kind = value;
                    break;
                case "data":
                    data = value;
                    break;
            }
        }
    }

    /// <summary>Reads events until <paramref name="done"/> returns true for the collected list.</summary>
    public async Task<List<SseEvent>> ReadUntilAsync(Func<List<SseEvent>, bool> done)
    {
        var events = new List<SseEvent>();
        while (!done(events))
        {
            events.Add(await NextAsync());
        }

        return events;
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        _response.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed record SseEvent(string? Id, string Kind, JsonElement Data)
{
    public string? RunId => Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty("runId", out var r) ? r.GetString() : null;

    public long Sequence => Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty("sequence", out var s) ? s.GetInt64() : 0;

    public bool IsRunCompletion(string runId) =>
        Kind == "execution.completed" && RunId == runId && !Data.TryGetProperty("parentExecutionId", out _);
}
