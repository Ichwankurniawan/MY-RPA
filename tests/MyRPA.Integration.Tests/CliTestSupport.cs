using System.Diagnostics;
using System.Runtime.InteropServices;
using MyRPA.Cli;

namespace MyRPA.Integration.Tests;

public sealed record CliResult(int ExitCode, string Out, string Error);

/// <summary>Runs the CLI in-process (same host and DI graph as the executable).</summary>
public static class Cli
{
    public static async Task<CliResult> RunAsync(params string[] args) =>
        await RunAsync(TestContext.Current.CancellationToken, args);

    public static async Task<CliResult> RunAsync(CancellationToken cancellationToken, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await CliApplication.RunAsync(args, stdout, stderr, cancellationToken);
        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
    }
}

/// <summary>Runs the real <c>myrpa</c> executable as a child process (for stdout/stderr and working-directory behavior).</summary>
public static class CliProcess
{
    public static async Task<CliResult> RunAsync(string workingDirectory, params string[] args)
    {
        var cliDll = Path.Combine(AppContext.BaseDirectory, "myrpa.dll");
        Assert.True(File.Exists(cliDll), $"CLI not found at {cliDll}");

        // The dotnet root is three levels above the shared runtime directory (…/shared/Microsoft.NETCore.App/<version>/).
        var dotnetRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var dotnet = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(cliDll);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["DOTNET_ROOT"] = dotnetRoot;
        start.Environment.Remove("DOTNET_ENVIRONMENT");

        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return new CliResult(process.ExitCode, await stdout, await stderr);
    }
}

/// <summary>A temporary directory with workflow files, deleted on dispose.</summary>
public sealed class TempWorkspace : IDisposable
{
    public TempWorkspace() => Directory.CreateDirectory(Root);

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "myrpa-it-" + Guid.NewGuid().ToString("N"));

    public string Write(string relative, string content)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public static string Workflow(string root, string arguments = "[]", string id = "wf") =>
        $$"""{ "schemaVersion": "1.0", "id": "{{id}}", "name": "Test", "version": "1.0.0", "arguments": {{arguments}}, "root": {{root}} }""";

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

/// <summary>Repository paths (for shipped samples).</summary>
public static class RepositoryPaths
{
    public static string Root { get; } = Find();

    public static string Samples => Path.Combine(Root, "samples");

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MyRPA.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
