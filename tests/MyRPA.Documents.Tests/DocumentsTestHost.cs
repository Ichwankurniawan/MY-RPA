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

namespace MyRPA.Documents.Tests;

/// <summary>
/// The documents plugin loaded through the real plugin host over a temporary file root (with an "outside" folder next
/// to it), with small limits and every log message captured.
/// </summary>
public sealed class DocumentsHost : IAsyncLifetime
{
    public const int MaxFileBytes = 64 * 1024;

    public const int MaxPages = 3;

    private static int _counter;

    private readonly string _parent = Directory.CreateTempSubdirectory("myrpa-documents-").FullName;
    private ServiceProvider? _services;

    public DocumentsHost()
    {
        FileRoot = Directory.CreateDirectory(Path.Combine(_parent, "root")).FullName;
        Outside = Directory.CreateDirectory(Path.Combine(_parent, "outside")).FullName;
    }

    public PluginSet Plugins { get; private set; } = null!;

    public string FileRoot { get; }

    public string Outside { get; }

    public CapturingLoggerProvider Logs { get; } = new();

    public IServiceProvider Services => _services!;

    public async ValueTask InitializeAsync()
    {
        var options = new PluginHostOptions();
        var source = new PluginSource { Directory = DocumentsPaths.Plugin };
        source.Settings["fileRoot"] = FileRoot;
        source.Settings["maxFileBytes"] = $"{MaxFileBytes}";
        source.Settings["maxPages"] = $"{MaxPages}";
        options.Sources.Add(source);
        Plugins = await PluginLoader.LoadAsync(options, TestContext.Current.CancellationToken);
        Assert.False(Plugins.HasRequiredFailures, string.Join(Environment.NewLine, Plugins.Diagnostics));

        _services = new ServiceCollection().AddLogging(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace)).AddMyRpaRuntime().AddMyRpaActivities().AddMyRpaPlugins(Plugins)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await Plugins.DisposeAsync();
        try
        {
            Directory.Delete(_parent, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a temporary folder left behind does not change any result.
        }
    }

    public string InRoot(string relative) => Path.Combine(FileRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    public WorkflowLoadResult Load(string nodes, IEnumerable<string> outputs, IEnumerable<string> inputs)
    {
        var arguments = string.Join(", ",
            inputs.Select(i => $$"""{ "name": "{{i}}", "direction": "In", "type": "Object" }""")
                .Concat(outputs.Select(o => $$"""{ "name": "{{o}}", "direction": "Out", "type": "Object" }""")));
        var json = $$"""
            { "schemaVersion": "1.0", "id": "documents-test", "name": "Documents test", "version": "1.0.0",
              "arguments": [ {{arguments}} ],
              "root": { "id": "main", "type": "Core.Sequence", "children": [ {{nodes}} ] } }
            """;
        return Services.GetRequiredService<WorkflowLoader>().Load(json);
    }

    /// <summary>Runs a Sequence of <paramref name="nodes"/>; <paramref name="inputs"/> are In arguments of type Object.</summary>
    public Task<WorkflowExecutionResult> RunAsync(string nodes, string[]? outputs = null, IReadOnlyDictionary<string, object?>? inputs = null)
    {
        var load = Load(nodes, outputs ?? [], inputs?.Keys ?? []);
        Assert.True(load.IsValid, string.Join(Environment.NewLine, load.Diagnostics));
        return Services.GetRequiredService<IWorkflowRunner>().RunAsync(
            load.Workflow!, new WorkflowRunRequest { Arguments = inputs ?? new Dictionary<string, object?>() }, TestContext.Current.CancellationToken);
    }

    public static string Node(string type, string properties) =>
        $$"""{ "id": "d{{Interlocked.Increment(ref _counter)}}", "type": "{{type}}", "properties": { {{properties}} } }""";

    public static void AssertSucceeded(WorkflowExecutionResult result) =>
        Assert.True(result.Status == ExecutionStatus.Succeeded, $"{result.Status}: {result.Error?.ErrorType} {result.Error?.Message}");

    /// <summary>Asserts the failure's error type, and that its message never reveals the absolute root.</summary>
    public void AssertFailed(WorkflowExecutionResult result, string errorType)
    {
        Assert.True(result.Status == ExecutionStatus.Failed, $"Expected Failed/{errorType} but was {result.Status}: {result.Error?.ErrorType} {result.Error?.Message}");
        Assert.Equal(errorType, result.Error!.ErrorType);
        Assert.DoesNotContain(FileRoot, result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public static class DocumentsPaths
{
    public static string Plugin { get; } = Path.GetFullPath(
        typeof(DocumentsPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "DocumentsPluginDirectory").Value!);
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
    public const string InvalidInput = "InvalidInput";
    public const string InvalidDocument = "InvalidDocument";
    public const string EncryptedDocument = "EncryptedDocument";
    public const string FileAccessDenied = "FileAccessDenied";
    public const string FileNotFound = "FileNotFound";
    public const string FileTooLarge = "FileTooLarge";
    public const string TooManyItems = "TooManyItems";
}
