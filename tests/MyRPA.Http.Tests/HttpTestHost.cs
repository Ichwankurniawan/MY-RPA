using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyRPA.Activities;
using MyRPA.Core.Execution;
using MyRPA.Plugins;
using MyRPA.Runtime;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Validation;

namespace MyRPA.Http.Tests;

/// <summary>
/// The HTTP plugin loaded through the real plugin host (allowedHosts = localhost, small limits), with two local test
/// servers on different ports (two origins) and every log message captured.
/// </summary>
public sealed class HttpHost : IAsyncLifetime
{
    public const int MaxResponseBytes = 64 * 1024;

    public const int MaxRedirects = 3;

    private static int _counter;

    private ServiceProvider? _services;

    public PluginSet Plugins { get; private set; } = null!;

    public TestServer Server { get; } = new();

    /// <summary>A second origin (another port) for cross-origin redirects.</summary>
    public TestServer Other { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    public IServiceProvider Services => _services!;

    public async ValueTask InitializeAsync()
    {
        var options = new PluginHostOptions();
        var source = new PluginSource { Directory = HttpPaths.Plugin };
        source.Settings["allowedHosts"] = "localhost";
        source.Settings["maxResponseBytes"] = $"{MaxResponseBytes}";
        source.Settings["maxRedirects"] = $"{MaxRedirects}";
        options.Sources.Add(source);
        Plugins = await PluginLoader.LoadAsync(options, TestContext.Current.CancellationToken);
        Assert.False(Plugins.HasRequiredFailures, string.Join(Environment.NewLine, Plugins.Diagnostics));

        _services = new ServiceCollection()
            .AddLogging(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace))
            .AddMyRpaRuntime().AddMyRpaActivities().AddMyRpaPlugins(Plugins)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await Plugins.DisposeAsync();
        Server.Dispose();
        Other.Dispose();
    }

    public WorkflowLoadResult Load(string nodes, IEnumerable<string> outputs, IEnumerable<string> inputs)
    {
        var arguments = string.Join(", ",
            inputs.Select(i => $$"""{ "name": "{{i}}", "direction": "In", "type": "Object" }""")
                .Concat(outputs.Select(o => $$"""{ "name": "{{o}}", "direction": "Out", "type": "Object" }""")));
        var json = $$"""
            { "schemaVersion": "1.0", "id": "http-test", "name": "HTTP test", "version": "1.0.0",
              "arguments": [ {{arguments}} ],
              "root": { "id": "main", "type": "Core.Sequence", "children": [ {{nodes}} ] } }
            """;
        return Services.GetRequiredService<WorkflowLoader>().Load(json);
    }

    /// <summary>Runs a Sequence of <paramref name="nodes"/>; <paramref name="secrets"/> are its In arguments (secrets, headers…).</summary>
    public Task<WorkflowExecutionResult> RunAsync(string nodes, string[]? outputs = null, IReadOnlyDictionary<string, object?>? secrets = null, CancellationToken? cancellationToken = null)
    {
        var load = Load(nodes, outputs ?? [], secrets?.Keys ?? []);
        Assert.True(load.IsValid, string.Join(Environment.NewLine, load.Diagnostics));
        return Services.GetRequiredService<IWorkflowRunner>().RunAsync(
            load.Workflow!,
            new WorkflowRunRequest { Arguments = secrets ?? new Dictionary<string, object?>() },
            cancellationToken ?? TestContext.Current.CancellationToken);
    }

    /// <summary>An Http.Request node; <paramref name="properties"/> is the inside of its properties object.</summary>
    public static string Request(string properties) =>
        $$"""{ "id": "r{{Interlocked.Increment(ref _counter)}}", "type": "Http.Request", "properties": { {{properties}} } }""";

    public static void AssertSucceeded(WorkflowExecutionResult result) =>
        Assert.True(result.Status == ExecutionStatus.Succeeded, $"{result.Status}: {result.Error?.ErrorType} {result.Error?.Message}");

    public static void AssertFailed(WorkflowExecutionResult result, string errorType)
    {
        Assert.True(result.Status == ExecutionStatus.Failed, $"Expected Failed/{errorType} but was {result.Status}: {result.Error?.ErrorType} {result.Error?.Message}");
        Assert.Equal(errorType, result.Error!.ErrorType);
    }
}

public static class HttpPaths
{
    public static string Plugin { get; } = Path.GetFullPath(
        typeof(HttpPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "HttpPluginDirectory").Value!);
}

/// <summary>Captures every log message (with exceptions) so tests can prove secrets never reach a log.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyCollection<string> Messages => _messages;

    public ILogger CreateLogger(string categoryName) => new Logger(_messages);

    public void Dispose()
    {
    }

    private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Enqueue(formatter(state, exception) + " " + state + " " + exception);
    }
}

/// <summary>The errorType values workflows see (ADR-0042), as strings: the tests never compile against the plugin.</summary>
public static class ErrorTypes
{
    public const string InvalidUrl = "InvalidUrl";
    public const string HostNotAllowed = "HostNotAllowed";
    public const string HttpStatus = "HttpStatus";
    public const string HttpConnection = "HttpConnection";
    public const string Timeout = "Timeout";
    public const string ResponseTooLarge = "ResponseTooLarge";
    public const string TooManyRedirects = "TooManyRedirects";
    public const string InvalidJson = "InvalidJson";
    public const string InvalidInput = "InvalidInput";
}
