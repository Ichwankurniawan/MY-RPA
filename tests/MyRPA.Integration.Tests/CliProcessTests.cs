using System.Text.Json;
using MyRPA.Cli;

namespace MyRPA.Integration.Tests;

/// <summary>
/// Runs the real executable in a child process to verify behavior that in-process tests cannot see (Phase 1 review M5):
/// stdout/stderr separation and isolation from the caller's working directory.
/// </summary>
public sealed class CliProcessTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static bool IsLogLine(string line) =>
        line.StartsWith("trce:", StringComparison.Ordinal) || line.StartsWith("dbug:", StringComparison.Ordinal)
        || line.StartsWith("info:", StringComparison.Ordinal) || line.StartsWith("warn:", StringComparison.Ordinal)
        || line.StartsWith("fail:", StringComparison.Ordinal) || line.StartsWith("crit:", StringComparison.Ordinal);

    [Fact]
    public async Task VerboseLogs_GoToStandardError_NeverStandardOutput()
    {
        var result = await CliProcess.RunAsync(_workspace.Root, "--verbose", "info");

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.DoesNotContain(result.Out.Split('\n'), IsLogLine);
        Assert.Contains(result.Error.Split('\n'), l => l.StartsWith("dbug:", StringComparison.Ordinal));
        Assert.Contains("myrpa.execution.id=", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_WorkflowLogMessages_GoToStandardError_AndStdoutIsPureJson()
    {
        var path = _workspace.Write("log.json", TempWorkspace.Workflow("""{ "id": "say", "type": "Core.Log", "properties": { "message": "'visible to the user'" } }"""));

        var result = await CliProcess.RunAsync(_workspace.Root, "run", path);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("visible to the user", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("visible to the user", result.Out, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(result.Out);
        Assert.Equal("Succeeded", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Configuration_IsNotReadFromTheCurrentDirectory()
    {
        // If the CLI read appsettings.json from its working directory, this would enable Trace logging on stderr.
        _workspace.Write("appsettings.json", """{ "Logging": { "LogLevel": { "Default": "Trace" } } }""");

        var result = await CliProcess.RunAsync(_workspace.Root, "info");

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task ExitCodes_ArePropagatedToTheProcess()
    {
        var invalid = _workspace.Write("bad.json", "{ not json");

        Assert.Equal(CliExitCodes.InvalidWorkflow, (await CliProcess.RunAsync(_workspace.Root, "validate", invalid)).ExitCode);
        Assert.Equal(CliExitCodes.Usage, (await CliProcess.RunAsync(_workspace.Root, "nonsense")).ExitCode);
    }
}
