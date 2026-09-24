using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MyRPA.Activities;
using MyRPA.Core.Execution;
using MyRPA.Plugins;
using MyRPA.Runtime;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Validation;

namespace MyRPA.Browser.Playwright.Tests;

/// <summary>
/// Browser tests launch real browsers and count browser processes: every test class is in this collection, so they run
/// one at a time (a test may still run several browsers concurrently itself).
/// </summary>
public static class BrowserTestGroup
{
    public const string Name = "Browser";
}

public static class BrowserPaths
{
    public static string Plugin { get; } = Path.GetFullPath(
        typeof(BrowserPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "BrowserPluginDirectory").Value!);
}

/// <summary>
/// The browser plugin loaded through the real plugin host, with the real runtime and built-in activities, plus a local
/// test site and a temporary file root. Shared by the tests of one class (loading hashes ~100 MB of plugin content).
/// </summary>
public sealed class BrowserHost : IAsyncLifetime
{
    private ServiceProvider? _services;

    public PluginSet Plugins { get; private set; } = null!;

    public TestSite Site { get; } = new();

    public string FileRoot { get; } = Directory.CreateTempSubdirectory("myrpa-browser-files-").FullName;

    public IServiceProvider Services => _services!;

    public async ValueTask InitializeAsync()
    {
        var options = new PluginHostOptions();
        var source = new PluginSource { Directory = BrowserPaths.Plugin };
        source.Settings["fileRoot"] = FileRoot;
        source.Settings["defaultTimeoutMilliseconds"] = "5000";
        options.Sources.Add(source);
        Plugins = await PluginLoader.LoadAsync(options, TestContext.Current.CancellationToken);
        Assert.False(Plugins.HasRequiredFailures, string.Join(Environment.NewLine, Plugins.Diagnostics));

        _services = new ServiceCollection().AddLogging().AddMyRpaRuntime().AddMyRpaActivities().AddMyRpaPlugins(Plugins)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await Plugins.DisposeAsync();
        Site.Dispose();
        try
        {
            Directory.Delete(FileRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Runs a workflow whose root is a Sequence of <paramref name="nodes"/> with the given Out arguments.</summary>
    public Task<WorkflowExecutionResult> RunAsync(string nodes, string[]? outputs = null, TimeSpan? timeout = null, CancellationToken? cancellationToken = null)
    {
        var arguments = string.Join(", ", (outputs ?? []).Select(o => $$"""{ "name": "{{o}}", "direction": "Out", "type": "Object" }"""));
        var json = $$"""
            { "schemaVersion": "1.0", "id": "browser-test", "name": "Browser test", "version": "1.0.0",
              "arguments": [ {{arguments}} ],
              "root": { "id": "main", "type": "Core.Sequence", "children": [ {{nodes}} ] } }
            """;
        var load = Services.GetRequiredService<WorkflowLoader>().Load(json);
        Assert.True(load.IsValid, string.Join(Environment.NewLine, load.Diagnostics));
        return Services.GetRequiredService<IWorkflowRunner>().RunAsync(
            load.Workflow!, new WorkflowRunRequest { Timeout = timeout }, cancellationToken ?? TestContext.Current.CancellationToken);
    }

    /// <summary>A <c>Browser.Open</c> node on the test site's home page.</summary>
    public string Open(string path = "/", string id = "open", string extra = "") =>
        $$"""{ "id": "{{id}}", "type": "Browser.Open", "properties": { "url": "'{{Site.Url(path)}}'"{{extra}} } }""";
}

public static class Nodes
{
    private static int _counter;

    public static string Node(string type, string properties) =>
        $$"""{ "id": "n{{Interlocked.Increment(ref _counter)}}", "type": "{{type}}", "properties": { {{properties}} } }""";

    public static void AssertFailed(WorkflowExecutionResult result, string errorType)
    {
        Assert.True(result.Status == ExecutionStatus.Failed, $"Expected Failed/{errorType} but was {result.Status}: {result.Error?.ErrorType} {result.Error?.Message}");
        Assert.Equal(errorType, result.Error!.ErrorType);
    }

    public static void AssertSucceeded(WorkflowExecutionResult result) =>
        Assert.True(result.Status == ExecutionStatus.Succeeded, $"{result.Status}: {result.Error?.ErrorType} {result.Error?.Message}");
}

public static class BrowserProcesses
{
    /// <summary>Headless Chromium processes currently running on this machine.</summary>
    public static int Count() => Process.GetProcessesByName("chrome-headless-shell").Length;

    /// <summary>Waits briefly for browser processes to exit (process teardown is asynchronous).</summary>
    public static async Task<int> SettleAsync(int expected)
    {
        for (var i = 0; i < 50 && Count() > expected; i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return Count();
    }
}

/// <summary>
/// The errorType values workflows see (ADR-0017). Mirrored as strings on purpose: they are the stable contract, and the
/// tests never compile against the plugin.
/// </summary>
public static class ErrorTypes
{
    public const string ElementNotFound = "ElementNotFound";
    public const string AmbiguousMatch = "AmbiguousMatch";
    public const string InvalidSelector = "InvalidSelector";
    public const string OperationTimeout = "OperationTimeout";
    public const string ProviderUnavailable = "ProviderUnavailable";
    public const string BrowserLaunchFailed = "BrowserLaunchFailed";
    public const string NavigationFailed = "NavigationFailed";
    public const string InvalidUrl = "InvalidUrl";
    public const string ElementTimeout = "ElementTimeout";
    public const string SessionClosed = "SessionClosed";
    public const string SessionNotFound = "SessionNotFound";
    public const string DownloadFailed = "DownloadFailed";
    public const string FileAccessDenied = "FileAccessDenied";
    public const string FileNotFound = "FileNotFound";
    public const string FileAlreadyExists = "FileAlreadyExists";
    public const string InvalidArgument = "InvalidArgument";
}
