using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MyRPA.Cli;
using MyRPA.Cli.Commands;
using MyRPA.Core.Activities;
using MyRPA.Core.Diagnostics;
using MyRPA.Runtime.Diagnostics;

namespace MyRPA.Integration.Tests;

/// <summary>Runs the real CLI composition in-process (same host, DI graph and logging setup as the executable).</summary>
public sealed class CliHostTests
{
    private static async Task<(int ExitCode, string Out, string Error)> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await CliApplication.RunAsync(args, stdout, stderr, TestContext.Current.CancellationToken);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void BuildHost_ResolvesWholeGraph_WithValidation()
    {
        // ValidateOnBuild/ValidateScopes are enabled in BuildHost; building proves the graph is consistent.
        using var host = CliApplication.BuildHost(new CliOutput(TextWriter.Null, TextWriter.Null), verbose: false);

        Assert.NotNull(host.Services.GetRequiredService<CliCommandDispatcher>());
        Assert.NotNull(host.Services.GetRequiredService<IActivityCatalog>());
        Assert.NotNull(host.Services.GetRequiredService<IExecutionScopeFactory>());
        Assert.Contains(host.Services.GetServices<ICliCommand>(), c => c.Name == "info");
    }

    [Fact]
    public async Task Info_PrintsFoundationFacts_AndSucceeds()
    {
        var (exitCode, stdout, _) = await RunAsync("info");

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.StartsWith("MyRPA ", stdout, StringComparison.Ordinal);
        Assert.Contains("Workflow schema version: 1.0", stdout, StringComparison.Ordinal);
        Assert.Contains($"Activity source: {DiagnosticNames.RuntimeActivitySource}", stdout, StringComparison.Ordinal);
        Assert.Contains("Registered activity types: 0", stdout, StringComparison.Ordinal);
        Assert.Matches(@"Execution ID: [0-9a-f]{32}", stdout);
    }

    [Fact]
    public async Task Info_EmitsRuntimeSpanWithExecutionId()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == DiagnosticNames.RuntimeActivitySource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        var (exitCode, stdout, _) = await RunAsync("info");

        Assert.Equal(CliExitCodes.Success, exitCode);
        var executionId = stdout.Split('\n').Single(l => l.StartsWith("Execution ID: ", StringComparison.Ordinal))["Execution ID: ".Length..].Trim();
        Assert.Contains(stopped, a => a.OperationName == InfoCommand.OperationName
            && Equals(a.GetTagItem(DiagnosticNames.ExecutionIdKey), executionId)
            && a.GetTagItem(DiagnosticNames.CorrelationIdKey) is string);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("version")]
    public async Task Version_PrintsVersion(string arg)
    {
        var (exitCode, stdout, _) = await RunAsync(arg);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.StartsWith("MyRPA 0.1.0", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_ListsRegisteredCommands()
    {
        var (exitCode, stdout, _) = await RunAsync("--help");

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("info", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoArguments_ReturnsUsage()
    {
        var (exitCode, stdout, stderr) = await RunAsync();

        Assert.Equal(CliExitCodes.Usage, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("Usage:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCommand_ReturnsUsage_OnStandardError()
    {
        var (exitCode, stdout, stderr) = await RunAsync("run", "workflow.json");

        // "run" is a Phase 2 command and must not exist yet.
        Assert.Equal(CliExitCodes.Usage, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("Unknown command 'run'", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerboseOption_IsAcceptedBeforeOrAfterCommand()
    {
        Assert.Equal(CliExitCodes.Success, (await RunAsync("--verbose", "info")).ExitCode);
        Assert.Equal(CliExitCodes.Success, (await RunAsync("info", "--verbose")).ExitCode);
    }

    [Fact]
    public async Task CancelledToken_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["info"], stdout, stderr, cts.Token);

        Assert.Equal(CliExitCodes.Cancelled, exitCode);
    }
}
