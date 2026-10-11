using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MyRPA.Activities;
using MyRPA.Ai.Contracts;
using MyRPA.Plugins;
using MyRPA.Runtime;

namespace MyRPA.Ai.OpenAiCompatible.Tests;

/// <summary>What the endpoint received.</summary>
public sealed record Received(string PathAndQuery, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>One scripted answer of the endpoint.</summary>
public sealed record Reply(int Status, string Body, int DelayMs = 0, string? RetryAfter = null, string? Location = null);

/// <summary>A local OpenAI-compatible endpoint (HttpListener on localhost): answers in order, the last one repeats.</summary>
public sealed class FakeEndpoint : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Reply> _replies;
    private readonly ConcurrentQueue<Received> _received = new();
    private int _count;

    public FakeEndpoint(params Reply[] replies)
    {
        _replies = [.. replies];
        Port = FreePort();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public int Port { get; }

    public string BaseUrl => $"http://localhost:{Port}/v1";

    public IReadOnlyList<Received> Received => [.. _received];

    public static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public static string Completion(string content, string model = "served-model", string finish = "stop") =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            model,
            choices = new[] { new { message = new { role = "assistant", content }, finish_reason = finish } },
            usage = new { prompt_tokens = 12, completion_tokens = 3 },
        });

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
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var headers = context.Request.Headers.AllKeys.Where(k => k is not null).ToDictionary(k => k!.ToLowerInvariant(), k => context.Request.Headers[k]!);
            _received.Enqueue(new Received(context.Request.Url!.PathAndQuery, headers, body));
            var reply = _replies[Math.Min(Interlocked.Increment(ref _count) - 1, _replies.Count - 1)];
            if (reply.DelayMs > 0)
            {
                await Task.Delay(reply.DelayMs, _stop.Token);
            }

            context.Response.StatusCode = reply.Status;
            if (reply.RetryAfter is not null)
            {
                context.Response.Headers["Retry-After"] = reply.RetryAfter;
            }

            if (reply.Location is not null)
            {
                context.Response.Headers["Location"] = reply.Location;
            }

            var bytes = Encoding.UTF8.GetBytes(reply.Body);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException or IOException)
        {
            // The client went away (a timeout or cancellation test).
        }
    }
}

/// <summary>The plugin loaded through the real plugin host with some settings.</summary>
public sealed class ProviderHost : IAsyncDisposable
{
    private ServiceProvider? _services;

    private ProviderHost(PluginSet plugins)
    {
        Plugins = plugins;
    }

    public PluginSet Plugins { get; }

    public IAiProvider Provider => _services!.GetServices<IAiProvider>().Single();

    public static string PluginDirectory { get; } = Path.GetFullPath(
        typeof(ProviderHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "OpenAiPluginDirectory").Value!);

    /// <summary>A fresh environment variable name holding <paramref name="key"/> (or unset when null).</summary>
    public static string KeyVariable(string? key)
    {
        var name = "MYRPA_TEST_AI_KEY_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        Environment.SetEnvironmentVariable(name, key);
        return name;
    }

    public static async Task<ProviderHost> StartAsync(IReadOnlyDictionary<string, string> settings, bool expectFailure = false)
    {
        var options = new PluginHostOptions();
        var source = new PluginSource { Directory = PluginDirectory };
        foreach (var (name, value) in settings)
        {
            source.Settings[name] = value;
        }

        options.Sources.Add(source);
        var plugins = await PluginLoader.LoadAsync(options, TestContext.Current.CancellationToken);
        var host = new ProviderHost(plugins);
        if (!expectFailure)
        {
            Assert.False(plugins.HasRequiredFailures, string.Join(Environment.NewLine, plugins.Diagnostics));
            host._services = new ServiceCollection().AddLogging().AddMyRpaRuntime().AddMyRpaActivities().AddMyRpaPlugins(plugins)
                .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        }

        return host;
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await Plugins.DisposeAsync();
    }
}
