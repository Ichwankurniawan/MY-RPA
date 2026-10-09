using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MyRPA.Cli;

namespace MyRPA.Integration.Tests;

/// <summary>
/// Phase 7 definition of done (ADR-0042): the order-report sample runs through the CLI with the files, HTTP, Excel and
/// browser plugins, against a local API and portal: API → files → Excel → browser → API.
/// </summary>
public sealed class EnterpriseSampleTests
{
    private const string Token = "sample-token-7f3a";

    private static string Sample => Path.Combine(RepositoryPaths.Samples, "plugins", "enterprise-order-report.json");

    private static string PluginDirectory(string key) => Path.GetFullPath(
        typeof(EnterpriseSampleTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!);

    private static string Config(TempWorkspace workspace, string data)
    {
        static string Entry(string key, string settings) =>
            $$"""{ "directory": {{JsonSerializer.Serialize(PluginDirectory(key))}}, "settings": { {{settings}} } }""";

        var root = JsonSerializer.Serialize(data);
        return workspace.Write(
            "plugins.json",
            $$"""
            { "pluginConfigVersion": "1.0",
              "plugins": [
                {{Entry("FilesPluginDirectory", $"\"fileRoot\": {root}")}},
                {{Entry("HttpPluginDirectory", "\"allowedHosts\": \"localhost\"")}},
                {{Entry("SpreadsheetPluginDirectory", $"\"fileRoot\": {root}")}},
                {{Entry("BrowserPluginDirectory", $"\"fileRoot\": {root}")}}
              ] }
            """);
    }

    [Fact]
    public async Task OrderReport_RunsApiFilesExcelBrowserApi_ThroughTheCli()
    {
        using var workspace = new TempWorkspace();
        using var server = new OrderServer(Token);
        var data = Directory.CreateDirectory(Path.Combine(workspace.Root, "data")).FullName;
        var config = Config(workspace, data);

        var result = await Cli.RunAsync(
            "--plugin-config", config, "run", Sample,
            "--arg", $"apiBase={server.Url("api")}",
            "--arg", $"portalUrl={server.Url("portal")}",
            "--arg", $"apiToken={Token}");

        Assert.True(result.ExitCode == CliExitCodes.Success, result.Out + result.Error);
        var outputs = JsonDocument.Parse(result.Out).RootElement.GetProperty("outputs");
        Assert.Equal(2, outputs.GetProperty("openCount").GetInt64());
        Assert.Equal("CONF-2", outputs.GetProperty("confirmation").GetString());
        Assert.Equal(201, outputs.GetProperty("postStatus").GetInt64());

        Assert.True(File.Exists(Path.Combine(data, "orders", "orders.json")));
        Assert.StartsWith("id,customer,status,amount\r\n1,Ada,open,120.5\r\n", await File.ReadAllTextAsync(Path.Combine(data, "orders", "orders.csv"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(data, "orders", "report.xlsx")));

        var report = Assert.Single(server.Reports);
        using (var body = JsonDocument.Parse(report))
        {
            Assert.Equal(2, body.RootElement.GetProperty("openOrders").GetInt64());
            Assert.Equal("CONF-2", body.RootElement.GetProperty("confirmation").GetString());
        }

        Assert.DoesNotContain(Token, result.Out + result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrderReport_WithAWrongToken_FailsAtTheFirstRequestAndWritesNothing()
    {
        using var workspace = new TempWorkspace();
        using var server = new OrderServer(Token);
        var data = Directory.CreateDirectory(Path.Combine(workspace.Root, "data")).FullName;

        var result = await Cli.RunAsync(
            "--plugin-config", Config(workspace, data), "run", Sample,
            "--arg", $"apiBase={server.Url("api")}",
            "--arg", $"portalUrl={server.Url("portal")}",
            "--arg", "apiToken=wrong-token");

        Assert.Equal(CliExitCodes.Failure, result.ExitCode);
        var error = JsonDocument.Parse(result.Out).RootElement.GetProperty("error");
        Assert.Equal("HttpStatus", error.GetProperty("errorType").GetString());
        Assert.Equal("fetch-orders", error.GetProperty("nodeId").GetString());
        Assert.Empty(Directory.GetFileSystemEntries(data));
        Assert.DoesNotContain("wrong-token", result.Out + result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrderReport_IsValid_WithItsPlugins()
    {
        using var workspace = new TempWorkspace();
        var data = Directory.CreateDirectory(Path.Combine(workspace.Root, "data")).FullName;

        var result = await Cli.RunAsync("--plugin-config", Config(workspace, data), "validate", Sample);

        Assert.True(result.ExitCode == CliExitCodes.Success, result.Out + result.Error);
    }

    /// <summary>A local orders API (Bearer token) and a portal page on one HttpListener (localhost, random port).</summary>
    private sealed class OrderServer : IDisposable
    {
        private const string Orders = """
            [ { "id": 1, "customer": "Ada", "status": "open", "amount": 120.5 },
              { "id": 2, "customer": "Bob", "status": "closed", "amount": 80 },
              { "id": 3, "customer": "Cy", "status": "open", "amount": 42 } ]
            """;

        private const string Portal = """
            <!doctype html><html><head><title>Portal</title></head><body>
              <label for="count">Open orders</label><input id="count" />
              <button id="submit" onclick="document.getElementById('result').innerHTML = '<p id=&quot;confirmation&quot;>CONF-' + document.getElementById('count').value + '</p>'">Submit</button>
              <div id="result"></div>
            </body></html>
            """;

        private readonly string _token;
        private readonly HttpListener _listener = new();
        private readonly ConcurrentQueue<string> _reports = new();
        private readonly Task _loop;

        public OrderServer(string token)
        {
            _token = token;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                BaseUrl = $"http://localhost:{((IPEndPoint)probe.LocalEndpoint).Port}/";
            }

            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _loop = Task.Run(ServeAsync);
        }

        public string BaseUrl { get; }

        public IReadOnlyCollection<string> Reports => _reports;

        public string Url(string path) => BaseUrl + path;

        public void Dispose()
        {
            _listener.Close();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }
        }

        private async Task ServeAsync()
        {
            while (true)
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
                var path = request.Url!.AbsolutePath;
                if (path == "/portal")
                {
                    await WriteAsync(response, 200, Portal, "text/html");
                    return;
                }

                if (request.Headers["Authorization"] != "Bearer " + _token)
                {
                    await WriteAsync(response, 401, """{ "error": "unauthorized" }""", "application/json");
                    return;
                }

                switch ((request.HttpMethod, path))
                {
                    case ("GET", "/api/orders"):
                        await WriteAsync(response, 200, Orders, "application/json");
                        break;
                    case ("POST", "/api/reports"):
                        using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                        {
                            _reports.Enqueue(await reader.ReadToEndAsync());
                        }

                        await WriteAsync(response, 201, """{ "saved": true }""", "application/json");
                        break;
                    default:
                        await WriteAsync(response, 404, "{}", "application/json");
                        break;
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
            {
            }
        }

        private static async Task WriteAsync(HttpListenerResponse response, int status, string text, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            response.StatusCode = status;
            response.ContentType = contentType;
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }
    }
}
