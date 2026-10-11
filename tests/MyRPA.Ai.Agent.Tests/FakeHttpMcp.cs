using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace MyRPA.Ai.Agent.Tests;

/// <summary>
/// A fake MCP server over Streamable HTTP (HttpListener on localhost), written by hand: JSON responses to requests, 202
/// to notifications, a session id, 405 to the optional GET stream. It can require a bearer token or redirect everything.
/// </summary>
public sealed class FakeHttpMcp : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    public FakeHttpMcp(string? token = null, string? redirectTo = null)
    {
        Token = token;
        RedirectTo = redirectTo;
        Port = FreePort();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public int Port { get; }

    public string Url => $"http://localhost:{Port}/mcp";

    public string? Token { get; }

    public string? RedirectTo { get; }

    public ConcurrentQueue<(string Method, string? Rpc, string? Authorization)> Received { get; } = new();

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
        var response = context.Response;
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var message = body.Length > 0 ? JsonNode.Parse(body) as JsonObject : null;
            Received.Enqueue((context.Request.HttpMethod, message?["method"]?.GetValue<string>(), context.Request.Headers["Authorization"]));
            if (RedirectTo is not null)
            {
                response.StatusCode = 307;
                response.Headers["Location"] = RedirectTo;
                response.Close();
                return;
            }

            if (Token is not null && context.Request.Headers["Authorization"] != "Bearer " + Token)
            {
                response.StatusCode = 401;
                response.Close();
                return;
            }

            if (context.Request.HttpMethod != "POST")
            {
                response.StatusCode = context.Request.HttpMethod == "DELETE" ? 200 : 405;
                response.Close();
                return;
            }

            var id = message?["id"]?.DeepClone();
            if (id is null)
            {
                response.StatusCode = 202;
                response.Close();
                return;
            }

            var method = message!["method"]!.GetValue<string>();
            JsonNode? result = method switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = message["params"]?["protocolVersion"]?.DeepClone() ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "fake-http-mcp", ["version"] = "1.0.0" },
                },
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject
                {
                    ["tools"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "echo",
                        ["description"] = "Echoes a text.",
                        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("text") },
                    }),
                },
                "tools/call" => new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message["params"]!["arguments"]!["text"]!.GetValue<string>() }),
                    ["structuredContent"] = new JsonObject { ["echo"] = message["params"]!["arguments"]!["text"]!.GetValue<string>() },
                },
                _ => null,
            };
            var reply = result is null
                ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" } }
                : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            if (method == "initialize")
            {
                response.Headers["Mcp-Session-Id"] = "session-1";
            }

            var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException or InvalidOperationException)
        {
            // The client went away.
        }
    }
}
