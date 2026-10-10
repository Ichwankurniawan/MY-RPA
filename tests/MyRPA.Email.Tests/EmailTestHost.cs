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

namespace MyRPA.Email.Tests;

/// <summary>
/// The email plugin loaded through the real plugin host with the given settings (each test points it at its own fake
/// server), a temporary file root and every log message captured.
/// </summary>
public sealed class EmailHost : IAsyncDisposable
{
    private static int _counter;

    private readonly string _parent;
    private ServiceProvider? _services;
    private PluginSet? _plugins;

    private EmailHost(string parent)
    {
        _parent = parent;
        FileRoot = Directory.CreateDirectory(Path.Combine(parent, "root")).FullName;
        Outside = Directory.CreateDirectory(Path.Combine(parent, "outside")).FullName;
    }

    public string FileRoot { get; }

    public string Outside { get; }

    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Loads the plugin with <paramref name="settings"/>; "fileRoot" may be "$root" for the host's temporary root.</summary>
    public static async Task<EmailHost> StartAsync(IReadOnlyDictionary<string, string> settings)
    {
        var host = new EmailHost(Directory.CreateTempSubdirectory("myrpa-email-").FullName);
        var options = new PluginHostOptions();
        var source = new PluginSource { Directory = EmailPaths.Plugin };
        foreach (var (name, value) in settings)
        {
            source.Settings[name] = value == "$root" ? host.FileRoot : value;
        }

        options.Sources.Add(source);
        host._plugins = await PluginLoader.LoadAsync(options, TestContext.Current.CancellationToken);
        Assert.False(host._plugins.HasRequiredFailures, string.Join(Environment.NewLine, host._plugins.Diagnostics));
        host._services = new ServiceCollection().AddLogging(b => b.AddProvider(host.Logs).SetMinimumLevel(LogLevel.Trace))
            .AddMyRpaRuntime().AddMyRpaActivities().AddMyRpaPlugins(host._plugins)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        if (_plugins is not null)
        {
            await _plugins.DisposeAsync();
        }

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
            { "schemaVersion": "1.0", "id": "email-test", "name": "Email test", "version": "1.0.0",
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

    public static string Node(string type, string properties) =>
        $$"""{ "id": "e{{Interlocked.Increment(ref _counter)}}", "type": "{{type}}", "properties": { {{properties}} } }""";

    public static void AssertSucceeded(WorkflowExecutionResult result) =>
        Assert.True(result.Status == ExecutionStatus.Succeeded, $"{result.Status}: {result.Error?.ErrorType} {result.Error?.Message}");

    public void AssertFailed(WorkflowExecutionResult result, string errorType)
    {
        Assert.True(result.Status == ExecutionStatus.Failed, $"Expected Failed/{errorType} but was {result.Status}: {result.Error?.ErrorType} {result.Error?.Message}");
        Assert.Equal(errorType, result.Error!.ErrorType);
        Assert.DoesNotContain(FileRoot, result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public static class EmailPaths
{
    public static string Plugin { get; } = Path.GetFullPath(
        typeof(EmailPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "EmailPluginDirectory").Value!);
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
    public const string HostNotConfigured = "HostNotConfigured";
    public const string EmailConnection = "EmailConnection";
    public const string EmailAuthentication = "EmailAuthentication";
    public const string EmailRejected = "EmailRejected";
    public const string FolderNotFound = "FolderNotFound";
    public const string MessageNotFound = "MessageNotFound";
    public const string MessageTooLarge = "MessageTooLarge";
    public const string Timeout = "Timeout";
    public const string InvalidInput = "InvalidInput";
    public const string FileAccessDenied = "FileAccessDenied";
    public const string FileAlreadyExists = "FileAlreadyExists";
}

/// <summary>
/// A real mail server for the IMAP tests, from MYRPA_TEST_MAIL ("smtpHost=…;smtpPort=…;imapHost=…;imapPort=…;
/// address=…;user=…;password=…", plain connections; CI runs GreenMail as a service container). Without it those tests are
/// reported as skipped, never as passed.
/// </summary>
public sealed record MailServerSettings(IReadOnlyDictionary<string, string> Values)
{
    public const string Variable = "MYRPA_TEST_MAIL";

    public static MailServerSettings? FromEnvironment()
    {
        var text = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return new MailServerSettings(text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : string.Empty, StringComparer.OrdinalIgnoreCase));
    }

    public string this[string name] => Values[name];

    /// <summary>The plugin settings for this server (plain connections, the host's temporary root).</summary>
    public Dictionary<string, string> PluginSettings() => new()
    {
        ["smtpHost"] = this["smtpHost"],
        ["smtpPort"] = this["smtpPort"],
        ["smtpSecurity"] = "None",
        ["imapHost"] = this["imapHost"],
        ["imapPort"] = this["imapPort"],
        ["imapSecurity"] = "None",
        ["fileRoot"] = "$root",
        ["defaultFrom"] = this["address"],
    };
}
