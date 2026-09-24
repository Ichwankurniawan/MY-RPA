using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MyRPA.Browser.Playwright.Tests;

/// <summary>A deterministic local web site for browser tests (HttpListener on localhost, random port).</summary>
public sealed class TestSite : IDisposable
{
    public const string Home = """
        <!doctype html>
        <html><head><title>Test site</title></head><body>
          <h1 id="title">Welcome</h1>
          <a id="link" href="/other" data-kind="nav">Other page</a>
          <label for="name">Name</label><input id="name" name="name" value="" />
          <button id="greet" onclick="document.getElementById('out').textContent = 'Hello, ' + document.getElementById('name').value">Greet</button>
          <p id="out"></p>
          <button id="hidden" style="display:none">Hidden</button>
          <button id="disabled" disabled>Disabled</button>
          <ul><li class="item">One</li><li class="item">Two</li></ul>
          <select id="color"><option value="r">Red</option><option value="g">Green</option><option value="b">Blue</option></select>
          <select id="multi" multiple><option value="a">A</option><option value="b">B</option><option value="c">C</option></select>
          <input id="file" type="file" multiple onchange="document.getElementById('files').textContent = [...this.files].map(f => f.name + ':' + f.size).join(',')" />
          <p id="files"></p>
          <a id="download" href="/file.txt">Download</a>
          <a id="nodownload" href="/other">Not a download</a>
          <div id="late"></div>
          <button id="vanish" onclick="this.remove()">Vanish</button>
          <script>setTimeout(() => { document.getElementById('late').innerHTML = '<span id="appeared">Appeared</span>'; }, 300);</script>
        </body></html>
        """;

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public TestSite()
    {
        var port = FreePort();
        BaseUrl = $"http://localhost:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    public string BaseUrl { get; }

    public string Url(string path) => BaseUrl + path.TrimStart('/');

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

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
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
        var response = context.Response;
        try
        {
            var path = context.Request.Url!.AbsolutePath;
            switch (path)
            {
                case "/":
                    await WriteAsync(response, Home);
                    break;
                case "/other":
                    await WriteAsync(response, "<!doctype html><html><head><title>Other</title></head><body><h1 id=\"title\">Other</h1></body></html>");
                    break;
                case "/slow":
                    await Task.Delay(TimeSpan.FromSeconds(20), _stop.Token);
                    await WriteAsync(response, "<html><body>slow</body></html>");
                    break;
                case "/file.txt":
                    response.AddHeader("Content-Disposition", "attachment; filename=\"report.txt\"");
                    await WriteAsync(response, "report-content", "text/plain");
                    break;
                case "/cookie/set":
                    response.AddHeader("Set-Cookie", "session=" + context.Request.QueryString["v"] + "; Path=/");
                    await WriteAsync(response, "<html><body><p id=\"done\">set</p></body></html>");
                    break;
                case "/cookie/get":
                    await WriteAsync(response, $"<html><body><p id=\"cookie\">{WebUtility.HtmlEncode(context.Request.Headers["Cookie"] ?? "(none)")}</p></body></html>");
                    break;
                default:
                    response.StatusCode = 404;
                    await WriteAsync(response, "<html><body>not found</body></html>");
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or OperationCanceledException or ObjectDisposedException)
        {
            // Client went away or the site is stopping.
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
            }
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, string body, string contentType = "text/html; charset=utf-8")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }
}
