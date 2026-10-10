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

namespace MyRPA.Database.Tests;

/// <summary>The database plugin loaded through the real plugin host with the given settings, logs captured.</summary>
public sealed class DatabaseHost : IAsyncDisposable
{
    private static int _counter;

    private ServiceProvider? _services;

    private DatabaseHost(PluginSet plugins)
    {
        Plugins = plugins;
    }

    public PluginSet Plugins { get; }

    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Loads the plugin; <paramref name="expectFailure"/> keeps a refused configuration for the test to inspect.</summary>
    public static async Task<DatabaseHost> StartAsync(IReadOnlyDictionary<string, string> settings, bool expectFailure = false)
    {
        var options = new PluginHostOptions();
        var source = new PluginSource { Directory = DatabasePaths.Plugin };
        foreach (var (name, value) in settings)
        {
            source.Settings[name] = value;
        }

        options.Sources.Add(source);
        var plugins = await PluginLoader.LoadAsync(options, TestContext.Current.CancellationToken);
        var host = new DatabaseHost(plugins);
        if (expectFailure)
        {
            return host;
        }

        Assert.False(plugins.HasRequiredFailures, string.Join(Environment.NewLine, plugins.Diagnostics));
        host._services = new ServiceCollection().AddLogging(b => b.AddProvider(host.Logs).SetMinimumLevel(LogLevel.Trace))
            .AddMyRpaRuntime().AddMyRpaActivities().AddMyRpaPlugins(plugins)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
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

    public WorkflowLoadResult Load(string nodes, IEnumerable<string> outputs, IEnumerable<string> inputs)
    {
        var arguments = string.Join(", ",
            inputs.Select(i => $$"""{ "name": "{{i}}", "direction": "In", "type": "Object" }""")
                .Concat(outputs.Select(o => $$"""{ "name": "{{o}}", "direction": "Out", "type": "Object" }""")));
        var json = $$"""
            { "schemaVersion": "1.0", "id": "db-test", "name": "Database test", "version": "1.0.0",
              "arguments": [ {{arguments}} ],
              "root": { "id": "main", "type": "Core.Sequence", "children": [ {{nodes}} ] } }
            """;
        return _services!.GetRequiredService<WorkflowLoader>().Load(json);
    }

    /// <summary>Runs a Sequence of <paramref name="nodes"/>; <paramref name="inputs"/> are In arguments of type Object.</summary>
    public Task<WorkflowExecutionResult> RunAsync(string nodes, string[]? outputs = null, IReadOnlyDictionary<string, object?>? inputs = null)
    {
        var load = Load(nodes, outputs ?? [], inputs?.Keys ?? []);
        Assert.True(load.IsValid, string.Join(Environment.NewLine, load.Diagnostics));
        return _services!.GetRequiredService<IWorkflowRunner>().RunAsync(
            load.Workflow!, new WorkflowRunRequest { Arguments = inputs ?? new Dictionary<string, object?>() }, TestContext.Current.CancellationToken);
    }

    /// <summary>A node; <paramref name="sql"/> is put in as a JSON string (the literal SQL text).</summary>
    public static string Node(string type, string connection, string sql, string? more = null) =>
        $$"""{ "id": "q{{Interlocked.Increment(ref _counter)}}", "type": "{{type}}", "properties": { "connection": "{{connection}}", "sql": {{System.Text.Json.JsonSerializer.Serialize(sql)}}{{(more is null ? string.Empty : ", " + more)}} } }""";

    public static void AssertSucceeded(WorkflowExecutionResult result) =>
        Assert.True(result.Status == ExecutionStatus.Succeeded, $"{result.Status}: {result.Error?.ErrorType} {result.Error?.Message}");

    public static void AssertFailed(WorkflowExecutionResult result, string errorType)
    {
        Assert.True(result.Status == ExecutionStatus.Failed, $"Expected Failed/{errorType} but was {result.Status}: {result.Error?.ErrorType} {result.Error?.Message}");
        Assert.Equal(errorType, result.Error!.ErrorType);
    }
}

public static class DatabasePaths
{
    public static string Plugin { get; } = Path.GetFullPath(
        typeof(DatabasePaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "DatabasePluginDirectory").Value!);
}

/// <summary>Captures every log message (with exceptions) so tests can prove a password never reaches a log.</summary>
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

/// <summary>The errorType values workflows see (ADR-0043), as strings: the tests never compile against the plugin.</summary>
public static class ErrorTypes
{
    public const string ConnectionNotFound = "ConnectionNotFound";
    public const string DatabaseConnection = "DatabaseConnection";
    public const string DatabaseError = "DatabaseError";
    public const string ReadOnlyConnection = "ReadOnlyConnection";
    public const string Timeout = "Timeout";
    public const string TooManyItems = "TooManyItems";
    public const string InvalidInput = "InvalidInput";
}
