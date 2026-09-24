using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using MyRPA.Activities;
using MyRPA.Plugins;
using MyRPA.Runtime;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Validation;
using static MyRPA.Browser.Playwright.Tests.Nodes;

namespace MyRPA.Browser.Playwright.Tests;

/// <summary>Uploads and downloads, confined to the plugin's file root.</summary>
[Collection(BrowserTestGroup.Name)]
public sealed class FileTests(BrowserHost host) : IClassFixture<BrowserHost>
{
    private string InRoot(string name, string? content = null)
    {
        var path = Path.Combine(host.FileRoot, name);
        if (content is not null)
        {
            File.WriteAllText(path, content);
        }

        return path;
    }

    // Forward slashes: valid Windows paths that need no escaping in JSON or in expression strings.
    private static string Json(string path) => path.Replace('\\', '/');

    [Fact]
    public async Task Upload_RelativeAndAbsolutePathsInsideTheRoot()
    {
        InRoot("a.txt", "12345");
        var absolute = InRoot("b.txt", "12");

        var result = await host.RunAsync(host.Open() + ", "
            + Node("Browser.UploadFile", $"\"selector\": \"#file\", \"files\": \"['a.txt', '{Json(absolute)}']\"") + ", "
            + Node("Browser.GetText", "\"selector\": \"#files\", \"to\": \"files\""), ["files"]);

        AssertSucceeded(result);
        Assert.Equal("a.txt:5,b.txt:2", result.Outputs["files"]);
    }

    [Theory]
    [InlineData("../outside.txt", ErrorTypes.FileAccessDenied)]
    [InlineData("C:/Windows/win.ini", ErrorTypes.FileAccessDenied)]
    [InlineData("missing.txt", ErrorTypes.FileNotFound)]
    public async Task Upload_OutsideTheRootOrMissing_IsRefused(string path, string errorType)
    {
        var result = await host.RunAsync(host.Open() + ", " + Node("Browser.UploadFile", $"\"selector\": \"#file\", \"files\": \"'{path}'\""));

        AssertFailed(result, errorType);
    }

    [Fact]
    public async Task Download_SavesTheFileInsideTheRoot()
    {
        var result = await host.RunAsync(host.Open() + ", "
            + Node("Browser.DownloadFile", "\"selector\": \"#download\", \"path\": \"'downloads-report.txt'\", \"to\": \"saved\""), ["saved"]);

        AssertSucceeded(result);
        Assert.Equal(Path.Combine(host.FileRoot, "downloads-report.txt"), result.Outputs["saved"]);
        Assert.Equal("report-content", await File.ReadAllTextAsync((string)result.Outputs["saved"]!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Download_DoesNotOverwriteUnlessAsked()
    {
        InRoot("existing.txt", "old");

        var refused = await host.RunAsync(host.Open() + ", " + Node("Browser.DownloadFile", "\"selector\": \"#download\", \"path\": \"'existing.txt'\""));
        var allowed = await host.RunAsync(host.Open() + ", " + Node("Browser.DownloadFile", "\"selector\": \"#download\", \"path\": \"'existing.txt'\", \"overwrite\": \"true\""));

        AssertFailed(refused, ErrorTypes.FileAlreadyExists);
        AssertSucceeded(allowed);
        Assert.Equal("report-content", await File.ReadAllTextAsync(InRoot("existing.txt"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("../escaped.txt", ErrorTypes.FileAccessDenied)]
    [InlineData("no-such-dir/report.txt", ErrorTypes.FileAccessDenied)]
    public async Task Download_OutsideTheRoot_IsRefusedBeforeDownloading(string path, string errorType)
    {
        var result = await host.RunAsync(host.Open() + ", " + Node("Browser.DownloadFile", $"\"selector\": \"#download\", \"path\": \"'{path}'\""));

        AssertFailed(result, errorType);
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(host.FileRoot, path))));
    }

    [Fact]
    public async Task Download_WhenTheClickStartsNoDownload_FailsWithDownloadFailed()
    {
        var result = await host.RunAsync(host.Open() + ", "
            + Node("Browser.DownloadFile", "\"selector\": \"#nodownload\", \"path\": \"'never.txt'\", \"timeoutMilliseconds\": 1500"));

        AssertFailed(result, ErrorTypes.DownloadFailed);
    }
}

/// <summary>The browser plugin through the real plugin host: load, initialize, register, execute, dispose, unload.</summary>
[Collection(BrowserTestGroup.Name)]
public sealed class BrowserPluginTests
{
    private static PluginSource Source() => new() { Directory = BrowserPaths.Plugin };

    [Fact]
    public async Task Plugin_LoadsAndRegistersItsDeclaredActivitiesAndProvider()
    {
        await using var plugins = await PluginLoader.LoadAsync(Options(Source()), TestContext.Current.CancellationToken);
        await using var services = Compose(plugins);

        var plugin = Assert.Single(plugins.Plugins);
        Assert.Empty(plugins.Diagnostics);
        Assert.Equal("MyRPA.Browser.Playwright", plugin.Manifest.Id.Value);
        Assert.Equal(["Browser.Playwright"], plugin.Manifest.Providers.Select(p => p.Value));
        var catalog = services.GetRequiredService<ActivityCatalog>();
        var browserActivities = catalog.Registrations.Where(r => r.Source == "MyRPA.Browser.Playwright").Select(r => r.Descriptor.TypeName.Value).Order(StringComparer.Ordinal);
        Assert.Equal(
            ["Browser.Click", "Browser.Close", "Browser.DownloadFile", "Browser.GetAttribute", "Browser.GetText", "Browser.Navigate",
             "Browser.Open", "Browser.SelectOption", "Browser.TypeText", "Browser.UploadFile", "Browser.WaitForElement"],
            browserActivities);
    }

    [Fact]
    public async Task PluginAssemblies_HaveARealLocationInsideThePluginDirectory()
    {
        // ADR-0016: Playwright finds its driver next to its assembly, so assemblies are loaded from their verified path.
        await using var plugins = await PluginLoader.LoadAsync(Options(Source()), TestContext.Current.CancellationToken);
        await using var services = Compose(plugins);
        using var site = new TestSite();

        var result = await Run(services, $$"""{ "id": "o", "type": "Browser.Open", "properties": { "url": "'{{site.BaseUrl}}'" } }""");

        AssertSucceeded(result);
        var context = AssemblyLoadContext.GetLoadContext(services.GetRequiredService<ActivityCatalog>().Registrations
            .First(r => r.Source is not null).ImplementationType.Assembly)!;
        Assert.Contains(context.Assemblies, a => a.GetName().Name == "Microsoft.Playwright");
        Assert.All(context.Assemblies, a => Assert.StartsWith(BrowserPaths.Plugin, a.Location, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WrongPin_IsRefused_BecauseTheDigestCoversTheDriver()
    {
        // The driver lives inside the plugin directory and is covered by its digest: a pinned plugin whose driver changed
        // is not loaded.
        await using var probe = await PluginLoader.LoadAsync(Options(Source()), TestContext.Current.CancellationToken);
        var digest = Assert.Single(probe.Plugins).Digest;
        var pinned = Source();
        pinned.Sha256 = new string('0', 64);

        await using var mismatch = await PluginLoader.LoadAsync(Options(pinned), TestContext.Current.CancellationToken);

        Assert.Equal(64, digest.Length);
        Assert.Equal(PluginDiagnosticCodes.IntegrityMismatch, Assert.Single(mismatch.Diagnostics).Code);
    }

    [Fact]
    public async Task Plugin_UnloadsAfterItsBrowsersAreClosed()
    {
        // Observed behaviour (ADR-0016): after the browser and the plugin are disposed, a plugin context is released once a
        // later load has happened; the most recent context may stay reachable a while longer, but they never accumulate.
        var first = await LoadRunAndDisposeAsync();
        var second = await LoadRunAndDisposeAsync();

        for (var i = 0; i < 100 && first.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.False(first.IsAlive, "The first browser plugin context was not unloaded.");
        GC.KeepAlive(second);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LoadRunAndDisposeAsync()
    {
        var plugins = await PluginLoader.LoadAsync(Options(Source()), TestContext.Current.CancellationToken);
        var services = Compose(plugins);
        using (var site = new TestSite())
        {
            AssertSucceeded(await Run(services, $$"""{ "id": "o", "type": "Browser.Open", "properties": { "url": "'{{site.BaseUrl}}'" } }"""));
        }

        var context = new WeakReference(AssemblyLoadContext.GetLoadContext(
            services.GetRequiredService<ActivityCatalog>().Registrations.First(r => r.Source is not null).ImplementationType.Assembly));
        await services.DisposeAsync();
        await plugins.DisposeAsync();
        return context;
    }

    private static PluginHostOptions Options(PluginSource source)
    {
        var options = new PluginHostOptions();
        options.Sources.Add(source);
        return options;
    }

    private static ServiceProvider Compose(PluginSet plugins) =>
        new ServiceCollection().AddLogging().AddMyRpaRuntime().AddMyRpaActivities().AddMyRpaPlugins(plugins).BuildServiceProvider();

    private static Task<WorkflowExecutionResult> Run(IServiceProvider services, string root)
    {
        var json = $$"""{ "schemaVersion": "1.0", "id": "p", "name": "P", "version": "1.0.0", "root": {{root}} }""";
        var load = services.GetRequiredService<WorkflowLoader>().Load(json);
        Assert.True(load.IsValid, string.Join(Environment.NewLine, load.Diagnostics));
        return services.GetRequiredService<IWorkflowRunner>().RunAsync(load.Workflow!, new WorkflowRunRequest(), TestContext.Current.CancellationToken);
    }
}

/// <summary>Concurrent browser workflows use separate browsers and never share cookies, pages or sessions.</summary>
[Collection(BrowserTestGroup.Name)]
public sealed class BrowserConcurrencyTests(BrowserHost host) : IClassFixture<BrowserHost>
{
    [Fact]
    public async Task ConcurrentRuns_AreIsolated()
    {
        const int runs = 5;
        var baseline = BrowserProcesses.Count();

        var results = await Task.WhenAll(Enumerable.Range(0, runs).Select(i => Task.Run(() => host.RunAsync(
            string.Join(", ",
                Node("Browser.Open", $"\"url\": \"'{host.Site.Url($"/cookie/set?v=run{i}")}'\", \"to\": \"session\""),
                Node("Browser.Navigate", $"\"url\": \"'{host.Site.Url("/cookie/get")}'\""),
                Node("Browser.GetText", "\"selector\": \"#cookie\", \"to\": \"cookie\""),
                Node("Browser.Navigate", $"\"url\": \"'{host.Site.BaseUrl}'\""),
                Node("Browser.TypeText", $"\"selector\": \"#name\", \"text\": \"'user{i}'\""),
                Node("Browser.Click", "\"selector\": \"#greet\""),
                Node("Browser.GetText", "\"selector\": \"#out\", \"to\": \"greeting\"")),
            ["session", "cookie", "greeting"]))));

        for (var i = 0; i < runs; i++)
        {
            AssertSucceeded(results[i]);
            Assert.Equal($"session=run{i}", results[i].Outputs["cookie"]);
            Assert.Equal($"Hello, user{i}", results[i].Outputs["greeting"]);
            // Session ids are per run, so every run's first session is browser-1.
            Assert.Equal("browser-1", results[i].Outputs["session"]);
        }

        Assert.Equal(runs, results.Select(r => r.ExecutionId).Distinct().Count());
        Assert.Equal(baseline, await BrowserProcesses.SettleAsync(baseline));
    }
}
