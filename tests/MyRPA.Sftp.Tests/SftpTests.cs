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

namespace MyRPA.Sftp.Tests;

/// <summary>The SFTP plugin loaded through the real plugin host with the given settings, a temporary file root and logs captured.</summary>
public sealed class SftpHost : IAsyncDisposable
{
    private static int _counter;

    private readonly string _parent;
    private ServiceProvider? _services;
    private PluginSet? _plugins;

    private SftpHost(string parent)
    {
        _parent = parent;
        FileRoot = Directory.CreateDirectory(Path.Combine(parent, "root")).FullName;
    }

    public PluginSet Plugins => _plugins!;

    public string FileRoot { get; }

    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Loads the plugin; a "fileRoot" of "$root" is the host's temporary root.</summary>
    public static async Task<SftpHost> StartAsync(IReadOnlyDictionary<string, string> settings, bool expectFailure = false)
    {
        var host = new SftpHost(Directory.CreateTempSubdirectory("myrpa-sftp-").FullName);
        var options = new PluginHostOptions();
        var source = new PluginSource { Directory = SftpPaths.Plugin };
        foreach (var (name, value) in settings)
        {
            source.Settings[name] = value == "$root" ? host.FileRoot : value;
        }

        options.Sources.Add(source);
        host._plugins = await PluginLoader.LoadAsync(options, TestContext.Current.CancellationToken);
        if (expectFailure)
        {
            return host;
        }

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
            { "schemaVersion": "1.0", "id": "sftp-test", "name": "SFTP test", "version": "1.0.0",
              "arguments": [ {{arguments}} ],
              "root": { "id": "main", "type": "Core.Sequence", "children": [ {{nodes}} ] } }
            """;
        return _services!.GetRequiredService<WorkflowLoader>().Load(json);
    }

    public Task<WorkflowExecutionResult> RunAsync(string nodes, string[]? outputs = null, IReadOnlyDictionary<string, object?>? inputs = null)
    {
        var load = Load(nodes, outputs ?? [], inputs?.Keys ?? []);
        Assert.True(load.IsValid, string.Join(Environment.NewLine, load.Diagnostics));
        return _services!.GetRequiredService<IWorkflowRunner>().RunAsync(
            load.Workflow!, new WorkflowRunRequest { Arguments = inputs ?? new Dictionary<string, object?>() }, TestContext.Current.CancellationToken);
    }

    /// <summary>A node on the server "box"; <paramref name="properties"/> is the rest of its properties object.</summary>
    public static string Node(string type, string properties) =>
        $$"""{ "id": "s{{Interlocked.Increment(ref _counter)}}", "type": "{{type}}", "properties": { "server": "box", {{properties}} } }""";

    public static void AssertSucceeded(WorkflowExecutionResult result) =>
        Assert.True(result.Status == ExecutionStatus.Succeeded, $"{result.Status}: {result.Error?.ErrorType} {result.Error?.Message}");

    public void AssertFailed(WorkflowExecutionResult result, string errorType)
    {
        Assert.True(result.Status == ExecutionStatus.Failed, $"Expected Failed/{errorType} but was {result.Status}: {result.Error?.ErrorType} {result.Error?.Message}");
        Assert.Equal(errorType, result.Error!.ErrorType);
        Assert.DoesNotContain(FileRoot, result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public static class SftpPaths
{
    public static string Plugin { get; } = Path.GetFullPath(
        typeof(SftpPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "SftpPluginDirectory").Value!);
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
    public const string ServerNotFound = "ServerNotFound";
    public const string HostKeyMismatch = "HostKeyMismatch";
    public const string SftpConnection = "SftpConnection";
    public const string SftpAuthentication = "SftpAuthentication";
    public const string RemotePathDenied = "RemotePathDenied";
    public const string RemoteFileNotFound = "RemoteFileNotFound";
    public const string RemoteFileExists = "RemoteFileExists";
    public const string Timeout = "Timeout";
    public const string InvalidInput = "InvalidInput";
    public const string FileAccessDenied = "FileAccessDenied";
    public const string FileTooLarge = "FileTooLarge";
}

/// <summary>What needs no server: configuration checks, remote root confinement and local paths, all refused before connecting.</summary>
public sealed class SftpOfflineTests
{
    private const string AnyKey = "SHA256:nThbg6kXUpJWGl7E1IGOCspRomTxdCARLviKw6E5SY8";

    private static Dictionary<string, object?> Login => new() { ["pwd"] = "secret" };

    private static Dictionary<string, string> Settings(params (string Name, string Value)[] extra)
    {
        var settings = new Dictionary<string, string>
        {
            ["server.box.host"] = "127.0.0.1",
            ["server.box.port"] = "1",
            ["server.box.hostKey"] = AnyKey,
            ["server.box.username"] = "robot",
            ["server.box.remoteRoot"] = "/upload",
            ["timeoutMs"] = "3000",
        };
        foreach (var (name, value) in extra)
        {
            settings[name] = value;
        }

        return settings;
    }

    [Theory]
    [InlineData("server.box.hostKey", "")]
    [InlineData("server.box.hostKey", "MD5:aa:bb")]
    [InlineData("server.box.remoteRoot", "relative/root")]
    public async Task ABadServerConfiguration_IsRefusedWhenThePluginLoads(string name, string value)
    {
        var settings = Settings();
        if (value.Length == 0)
        {
            settings.Remove(name);
        }
        else
        {
            settings[name] = value;
        }

        await using var host = await SftpHost.StartAsync(settings, expectFailure: true);

        Assert.True(host.Plugins.HasRequiredFailures);
    }

    [Theory]
    [InlineData("Sftp.List", """ "folder": "'../etc'", "password": "pwd", "result": "x" """)]
    [InlineData("Sftp.Delete", """ "remotePath": "'/etc/passwd'", "password": "pwd" """)]
    [InlineData("Sftp.Move", """ "from": "'a.txt'", "to": "'sub/../../b.txt'", "password": "pwd" """)]
    [InlineData("Sftp.CreateFolder", """ "folder": "'..'", "password": "pwd" """)]
    public async Task ARemotePathLeavingTheRemoteRoot_IsRefused_BeforeConnecting(string type, string properties)
    {
        await using var host = await SftpHost.StartAsync(Settings());

        var outputs = properties.Contains("\"result\"", StringComparison.Ordinal) ? new[] { "x" } : null;
        var result = await host.RunAsync(SftpHost.Node(type, properties), outputs, Login);

        host.AssertFailed(result, ErrorTypes.RemotePathDenied);
    }

    [Fact]
    public async Task ALocalPathOutsideTheRoot_IsRefused_BeforeConnecting()
    {
        await using var host = await SftpHost.StartAsync(Settings(("fileRoot", "$root")));

        var result = await host.RunAsync(SftpHost.Node("Sftp.Download", """ "remotePath": "'a.txt'", "localPath": "'../escape.txt'", "password": "pwd" """), inputs: Login);

        host.AssertFailed(result, ErrorTypes.FileAccessDenied);
    }

    [Fact]
    public async Task TransfersWithoutAFileRoot_AreRefused()
    {
        await using var host = await SftpHost.StartAsync(Settings());

        var result = await host.RunAsync(SftpHost.Node("Sftp.Upload", """ "localPath": "'a.txt'", "remotePath": "'a.txt'", "password": "pwd" """), inputs: Login);

        host.AssertFailed(result, ErrorTypes.FileAccessDenied);
        Assert.Contains("fileRoot", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownServer_FailsServerNotFound()
    {
        await using var host = await SftpHost.StartAsync(Settings());

        var result = await host.RunAsync("""{ "id": "x", "type": "Sftp.List", "properties": { "server": "other", "result": "x" } }""", ["x"]);

        host.AssertFailed(result, ErrorTypes.ServerNotFound);
        Assert.Contains("box", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoPasswordAndNoKey_FailsInvalidInput()
    {
        await using var host = await SftpHost.StartAsync(Settings());

        var result = await host.RunAsync(SftpHost.Node("Sftp.List", """ "result": "x" """), ["x"]);

        host.AssertFailed(result, ErrorTypes.InvalidInput);
    }

    [Fact]
    public async Task AServerThatCannotBeReached_FailsSftpConnectionOrTimeout()
    {
        await using var host = await SftpHost.StartAsync(Settings());

        var result = await host.RunAsync(SftpHost.Node("Sftp.List", """ "password": "pwd", "result": "x" """), ["x"], Login);

        Assert.True(result.Error!.ErrorType is ErrorTypes.SftpConnection or ErrorTypes.Timeout, $"{result.Error.ErrorType}: {result.Error.Message}");
    }

    [Fact]
    public async Task APasswordWrittenInTheWorkflow_IsRefusedByTheLoader()
    {
        await using var host = await SftpHost.StartAsync(Settings());

        var load = host.Load(SftpHost.Node("Sftp.List", """ "password": "'hunter2'", "result": "x" """), ["x"], []);

        Assert.False(load.IsValid);
        Assert.Contains(load.Diagnostics, d => d.Code == "MYRPA1066");
    }
}

/// <summary>
/// The SFTP activities against a real server (MYRPA_TEST_SFTP: "host=…;port=…;hostKey=SHA256:…;user=…;password=…;root=/…";
/// atmoz/sftp in CI). Skipped (never passed) without a server.
/// </summary>
public sealed class SftpServerTests
{
    private const string Pwd = """ "password": "pwd" """;

    private static Dictionary<string, string> Server()
    {
        var text = Environment.GetEnvironmentVariable("MYRPA_TEST_SFTP");
        if (string.IsNullOrWhiteSpace(text))
        {
            const string reason = "No SFTP server: set MYRPA_TEST_SFTP (see plugins/MyRPA.Sftp/README.md).";
            if (Environment.GetEnvironmentVariable("MYRPA_TEST_REQUIRE_SERVERS") == "1")
            {
                Assert.Fail(reason);
            }

            Assert.Skip(reason);
        }

        return text!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<(SftpHost Host, Dictionary<string, object?> Login)> StartAsync(Dictionary<string, string> server, string? hostKey = null, params (string Name, string Value)[] extra)
    {
        var settings = new Dictionary<string, string>
        {
            ["server.box.host"] = server["host"],
            ["server.box.port"] = server["port"],
            ["server.box.hostKey"] = hostKey ?? server["hostKey"],
            ["server.box.username"] = server["user"],
            ["server.box.remoteRoot"] = server["root"],
            ["fileRoot"] = "$root",
            ["timeoutMs"] = "20000",
        };
        foreach (var (name, value) in extra)
        {
            settings[name] = value;
        }

        return (await SftpHost.StartAsync(settings), new Dictionary<string, object?> { ["pwd"] = server["password"] });
    }

    private static string Unique(string stem) => $"{stem}{Guid.NewGuid():N}"[..(stem.Length + 8)];

    [Fact]
    public async Task UploadListDownloadMoveDelete_RoundTrip()
    {
        var server = Server();
        var (host, login) = await StartAsync(server);
        await using var _ = host;
        var folder = Unique("t");
        File.WriteAllText(host.InRoot("invoices.zip"), "zip bytes");

        var result = await host.RunAsync(
            SftpHost.Node("Sftp.CreateFolder", $$""" "folder": "'{{folder}}/daily'", {{Pwd}} """) + "," +
            SftpHost.Node("Sftp.Upload", $$""" "localPath": "'invoices.zip'", "remotePath": "'{{folder}}/daily/invoices.zip'", {{Pwd}}, "result": "uploaded" """) + "," +
            SftpHost.Node("Sftp.List", $$""" "folder": "'{{folder}}/daily'", {{Pwd}}, "result": "listed" """) + "," +
            SftpHost.Node("Sftp.Move", $$""" "from": "'{{folder}}/daily/invoices.zip'", "to": "'{{folder}}/archive.zip'", {{Pwd}} """) + "," +
            SftpHost.Node("Sftp.Download", $$""" "remotePath": "'{{folder}}/archive.zip'", "localPath": "'back/archive.zip'", {{Pwd}}, "result": "downloaded" """) + "," +
            SftpHost.Node("Sftp.Delete", $$""" "remotePath": "'{{folder}}/archive.zip'", {{Pwd}} """) + "," +
            SftpHost.Node("Sftp.Delete", $$""" "remotePath": "'{{folder}}/archive.zip'", "missingOk": true, {{Pwd}} """),
            ["uploaded", "listed", "downloaded"],
            login);

        SftpHost.AssertSucceeded(result);
        Assert.Equal($"{folder}/daily/invoices.zip", result.Outputs["uploaded"]);
        var entry = (IReadOnlyDictionary<string, object?>)Assert.Single((IReadOnlyList<object?>)result.Outputs["listed"]!)!;
        Assert.Equal("invoices.zip", entry["name"]);
        Assert.Equal(9L, entry["size"]);
        Assert.Equal(false, entry["isFolder"]);
        Assert.Equal("back/archive.zip", result.Outputs["downloaded"]);
        Assert.Equal("zip bytes", File.ReadAllText(host.InRoot("back/archive.zip")));
        Assert.Empty(Directory.GetFiles(host.FileRoot, "*.download", SearchOption.AllDirectories));
        Assert.DoesNotContain(host.Logs.Messages, m => m.Contains(server["password"], StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upload_NeverReplacesARemoteFile_UnlessOverwrite()
    {
        var server = Server();
        var (host, login) = await StartAsync(server);
        await using var _ = host;
        File.WriteAllText(host.InRoot("a.txt"), "first");
        var name = Unique("u") + ".txt";

        SftpHost.AssertSucceeded(await host.RunAsync(SftpHost.Node("Sftp.Upload", $$""" "localPath": "'a.txt'", "remotePath": "'{{name}}'", {{Pwd}} """), inputs: login));
        var refused = await host.RunAsync(SftpHost.Node("Sftp.Upload", $$""" "localPath": "'a.txt'", "remotePath": "'{{name}}'", {{Pwd}} """), inputs: login);
        var replaced = await host.RunAsync(SftpHost.Node("Sftp.Upload", $$""" "localPath": "'a.txt'", "remotePath": "'{{name}}'", "overwrite": true, {{Pwd}} """), inputs: login);
        var missing = await host.RunAsync(SftpHost.Node("Sftp.Download", $$""" "remotePath": "'no-such-{{name}}'", "localPath": "'x.txt'", {{Pwd}} """), inputs: login);
        await host.RunAsync(SftpHost.Node("Sftp.Delete", $$""" "remotePath": "'{{name}}'", {{Pwd}} """), inputs: login);

        host.AssertFailed(refused, ErrorTypes.RemoteFileExists);
        SftpHost.AssertSucceeded(replaced);
        host.AssertFailed(missing, ErrorTypes.RemoteFileNotFound);
        Assert.False(File.Exists(host.InRoot("x.txt")));
    }

    [Fact]
    public async Task AnotherHostKey_IsRefused_NamingThePresentedKey()
    {
        var server = Server();
        var (host, login) = await StartAsync(server, hostKey: "SHA256:nThbg6kXUpJWGl7E1IGOCspRomTxdCARLviKw6E5SY8");
        await using var _ = host;

        var result = await host.RunAsync(SftpHost.Node("Sftp.List", $$""" {{Pwd}}, "result": "x" """), ["x"], login);

        host.AssertFailed(result, ErrorTypes.HostKeyMismatch);
        Assert.Contains("SHA256:", result.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(server["password"], result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWrongPassword_FailsSftpAuthentication()
    {
        var server = Server();
        var (host, _) = await StartAsync(server);
        await using var __ = host;

        var result = await host.RunAsync(SftpHost.Node("Sftp.List", $$""" {{Pwd}}, "result": "x" """), ["x"], new Dictionary<string, object?> { ["pwd"] = "Wrong-Pa55word!" });

        host.AssertFailed(result, ErrorTypes.SftpAuthentication);
        Assert.DoesNotContain("Wrong-Pa55word!", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilesLargerThanMaxFileBytes_AreRefused_AndLeaveNoFile()
    {
        var server = Server();
        var (host, login) = await StartAsync(server, null, ("maxFileBytes", "10"));
        await using var _ = host;
        var name = Unique("b") + ".txt";
        File.WriteAllText(host.InRoot("fits.txt"), new string('x', 9));
        File.WriteAllText(host.InRoot("bigger.txt"), new string('x', 20));
        SftpHost.AssertSucceeded(await host.RunAsync(SftpHost.Node("Sftp.Upload", $$""" "localPath": "'fits.txt'", "remotePath": "'{{name}}'", {{Pwd}} """), inputs: login));

        var tooBig = await host.RunAsync(SftpHost.Node("Sftp.Upload", $$""" "localPath": "'bigger.txt'", "remotePath": "'{{name}}.2'", {{Pwd}} """), inputs: login);
        var fits = await host.RunAsync(SftpHost.Node("Sftp.Download", $$""" "remotePath": "'{{name}}'", "localPath": "'got.txt'", {{Pwd}} """), inputs: login);
        await host.RunAsync(SftpHost.Node("Sftp.Delete", $$""" "remotePath": "'{{name}}'", {{Pwd}} """), inputs: login);

        host.AssertFailed(tooBig, ErrorTypes.FileTooLarge);
        SftpHost.AssertSucceeded(fits);
        Assert.Equal(new string('x', 9), File.ReadAllText(host.InRoot("got.txt")));
        Assert.Empty(Directory.GetFiles(host.FileRoot, "*.download", SearchOption.AllDirectories));
    }
}
