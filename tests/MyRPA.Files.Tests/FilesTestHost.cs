using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MyRPA.Activities;
using MyRPA.Core.Execution;
using MyRPA.Plugins;
using MyRPA.Runtime;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Validation;

namespace MyRPA.Files.Tests;

/// <summary>
/// The files plugin loaded through the real plugin host, with the real runtime and built-in activities, over a temporary
/// file root (with an "outside" folder next to it). Small limits so the limit tests stay fast.
/// </summary>
public class FilesHost : IAsyncLifetime
{
    public const int MaxFileBytes = 4096;

    public const int MaxItems = 20;

    public const int MaxRows = 50;

    public const int MaxExtractBytes = 64 * 1024;

    private static int _counter;

    private readonly string _parent = Directory.CreateTempSubdirectory("myrpa-files-").FullName;
    private ServiceProvider? _services;

    public FilesHost()
    {
        FileRoot = Directory.CreateDirectory(Path.Combine(_parent, "root")).FullName;
        Outside = Directory.CreateDirectory(Path.Combine(_parent, "outside")).FullName;
        File.WriteAllText(Path.Combine(Outside, "secret.txt"), "outside");
    }

    public PluginSet Plugins { get; private set; } = null!;

    /// <summary>The plugin's fileRoot.</summary>
    public string FileRoot { get; }

    /// <summary>A folder beside the root that the activities must never reach.</summary>
    public string Outside { get; }

    public IServiceProvider Services => _services!;

    /// <summary>The runtime's clock when a test drives it (null: the system clock).</summary>
    public virtual FakeTimeProvider? Clock => null;

    public async ValueTask InitializeAsync()
    {
        var options = new PluginHostOptions();
        var source = new PluginSource { Directory = FilesPaths.Plugin };
        source.Settings["fileRoot"] = FileRoot;
        source.Settings["maxFileBytes"] = $"{MaxFileBytes}";
        source.Settings["maxItems"] = $"{MaxItems}";
        source.Settings["maxRows"] = $"{MaxRows}";
        source.Settings["maxExtractBytes"] = $"{MaxExtractBytes}";
        options.Sources.Add(source);
        Plugins = await PluginLoader.LoadAsync(options, TestContext.Current.CancellationToken);
        Assert.False(Plugins.HasRequiredFailures, string.Join(Environment.NewLine, Plugins.Diagnostics));

        var services = new ServiceCollection();
        if (Clock is not null)
        {
            services.AddSingleton<TimeProvider>(Clock);
        }

        _services = services.AddLogging().AddMyRpaRuntime().AddMyRpaActivities().AddMyRpaPlugins(Plugins)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await Plugins.DisposeAsync();
        Links.DeleteTree(_parent);
        GC.SuppressFinalize(this);
    }

    /// <summary>A path under the root.</summary>
    public string InRoot(string relative) => Path.Combine(FileRoot, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Runs a workflow whose root is a Sequence of <paramref name="nodes"/>, with Out arguments <paramref name="outputs"/>.</summary>
    public Task<WorkflowExecutionResult> RunAsync(string nodes, params string[] outputs)
    {
        var services = Services;
        var arguments = string.Join(", ", outputs.Select(o => $$"""{ "name": "{{o}}", "direction": "Out", "type": "Object" }"""));
        var json = $$"""
            { "schemaVersion": "1.0", "id": "files-test", "name": "Files test", "version": "1.0.0",
              "arguments": [ {{arguments}} ],
              "root": { "id": "main", "type": "Core.Sequence", "children": [ {{nodes}} ] } }
            """;
        var load = services.GetRequiredService<WorkflowLoader>().Load(json);
        Assert.True(load.IsValid, string.Join(Environment.NewLine, load.Diagnostics));
        return services.GetRequiredService<IWorkflowRunner>().RunAsync(load.Workflow!, new WorkflowRunRequest(), TestContext.Current.CancellationToken);
    }

    /// <summary>A node; <paramref name="properties"/> is the inside of its properties object.</summary>
    public static string Node(string type, string properties) =>
        $$"""{ "id": "n{{Interlocked.Increment(ref _counter)}}", "type": "{{type}}", "properties": { {{properties}} } }""";

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

/// <summary>The files host on a fake clock that tests advance while a run waits.</summary>
public sealed class ClockedFilesHost : FilesHost
{
    private readonly FakeTimeProvider _clock = new();

    public override FakeTimeProvider Clock => _clock;
}

public static class FilesPaths
{
    public static string Plugin { get; } = Path.GetFullPath(
        typeof(FilesPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "FilesPluginDirectory").Value!);
}

/// <summary>Creates directory links for the link tests: a symbolic link when allowed, a junction on Windows.</summary>
public static class Links
{
    /// <summary>A symbolic link to a directory, or null when this account may not create one.</summary>
    public static string? TrySymbolicLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return link;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A junction (Windows only; no privilege needed), or null elsewhere.</summary>
    public static string? TryJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link) ? link : null;
    }

    /// <summary>Deletes a temporary folder: links themselves first (never what they point to), then the rest.</summary>
    public static void DeleteTree(string folder)
    {
        try
        {
            foreach (var sub in Directory.GetDirectories(folder))
            {
                var info = new DirectoryInfo(sub);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    info.Delete();
                }
                else
                {
                    DeleteTree(sub);
                }
            }

            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a temporary folder left behind does not change any result.
        }
    }
}

/// <summary>The errorType values workflows see (ADR-0042), as strings: the tests never compile against the plugin.</summary>
public static class ErrorTypes
{
    public const string InvalidPath = "InvalidPath";
    public const string FileAccessDenied = "FileAccessDenied";
    public const string FileNotFound = "FileNotFound";
    public const string FileAlreadyExists = "FileAlreadyExists";
    public const string FileTooLarge = "FileTooLarge";
    public const string TooManyItems = "TooManyItems";
    public const string InvalidCsv = "InvalidCsv";
    public const string InvalidJson = "InvalidJson";
    public const string InvalidXml = "InvalidXml";
    public const string InvalidArchive = "InvalidArchive";
    public const string Timeout = "Timeout";
}
