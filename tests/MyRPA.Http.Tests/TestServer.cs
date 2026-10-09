using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MyRPA.Http.Tests;

/// <summary>What the server received.</summary>
public sealed record ReceivedRequest(string Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>
/// A deterministic local HTTP server (HttpListener on localhost, random port). Endpoints:
/// /echo (the request as JSON), /status/{code}, /text, /big, /big-chunked, /slow, /bad-json, /deep-json,
/// /redirect?to=URL&amp;code=N, /loop.
/// </summary>
public sealed class TestServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<ReceivedRequest> _received = new();
    private readonly Task _loop;

    public TestServer()
    {
        Port = FreePort();
        BaseUrl = $"http://localhost:{Port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    public int Port { get; }

    public string BaseUrl { get; }

    public IReadOnlyCollection<ReceivedRequest> Received => _received;

    public string Url(string path) => BaseUrl + path.TrimStart('/');

    /// <summary>The requests received for a path (without the query).</summary>
    public IReadOnlyList<ReceivedRequest> For(string path) => [.. _received.Where(r => r.PathAndQuery.Split('?')[0] == path)];

    public static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
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

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var headers = request.Headers.AllKeys.Where(k => k is not null).ToDictionary(k => k!.ToLowerInvariant(), k => request.Headers[k]!);
            var received = new ReceivedRequest(request.HttpMethod, request.Url!.PathAndQuery, headers, body);
            _received.Enqueue(received);

            var path = request.Url.AbsolutePath;
            if (request.HttpMethod == "HEAD")
            {
                response.ContentType = "text/plain";
                response.Close();
                return;
            }

            switch (path)
            {
                case "/echo":
                    await WriteAsync(response, JsonSerializer.Serialize(new { method = received.Method, query = request.Url.Query, body, headers }), "application/json");
                    break;
                case "/text":
                    await WriteAsync(response, "héllo", "text/plain; charset=utf-8");
                    break;
                case "/big":
                    await WriteAsync(response, new string('x', 100_000), "text/plain");
                    break;
                case "/big-chunked":
                    response.SendChunked = true;
                    response.ContentType = "text/plain";
                    for (var i = 0; i < 100; i++)
                    {
                        await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(new string('y', 1000)), _stop.Token);
                    }

                    response.Close();
                    break;
                case "/slow":
                    await Task.Delay(TimeSpan.FromSeconds(20), _stop.Token);
                    await WriteAsync(response, "slow", "text/plain");
                    break;
                case "/broken-error":
                    response.StatusCode = 500;
                    await WriteAsync(response, "{not json", "application/json");
                    break;
                case "/bad-json":
                    await WriteAsync(response, "{not json", "application/json");
                    break;
                case "/deep-json":
                    await WriteAsync(response, new string('[', 100) + new string(']', 100), "application/problem+json");
                    break;
                case "/redirect":
                    response.StatusCode = int.Parse(request.QueryString["code"] ?? "302", CultureInfo.InvariantCulture);
                    response.RedirectLocation = request.QueryString["to"];
                    response.Close();
                    break;
                case "/loop":
                    response.StatusCode = 302;
                    response.RedirectLocation = "/loop";
                    response.Close();
                    break;
                default:
                    if (path.StartsWith("/status/", StringComparison.Ordinal))
                    {
                        response.StatusCode = int.Parse(path["/status/".Length..], CultureInfo.InvariantCulture);
                        await WriteAsync(response, """{ "error": "nope" }""", "application/json");
                        break;
                    }

                    response.StatusCode = 404;
                    await WriteAsync(response, "not found", "text/plain");
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or OperationCanceledException or ObjectDisposedException or IOException)
        {
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }
}
