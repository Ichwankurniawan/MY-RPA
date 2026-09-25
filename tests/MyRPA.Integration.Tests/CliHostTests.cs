using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyRPA.Cli;
using MyRPA.Cli.Commands;
using MyRPA.Core.Diagnostics;
using MyRPA.Runtime.Diagnostics;
using MyRPA.Workflow.Execution;

namespace MyRPA.Integration.Tests;

/// <summary>Host composition and the non-workflow commands.</summary>
public sealed class CliHostTests
{
    [Fact]
    public void BuildHost_ResolvesWholeGraph_WithValidation()
    {
        // ValidateOnBuild/ValidateScopes are enabled in BuildHost; building proves the graph is consistent.
        using var host = CliApplication.BuildHost(new CliOutput(TextWriter.Null, TextWriter.Null), verbose: false);

        Assert.NotNull(host.Services.GetRequiredService<CliCommandDispatcher>());
        Assert.NotNull(host.Services.GetRequiredService<IWorkflowRunner>());
        Assert.NotNull(host.Services.GetRequiredService<IExecutionScopeFactory>());
        Assert.Equal(["catalog", "info", "plugins", "run", "validate"], host.Services.GetServices<ICliCommand>().Select(c => c.Name).Order());
        using var scope = host.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IWorkflowResolver>());
    }

    [Fact]
    public void BuildHost_ApplicationNameMatchesExecutable_AndContentRootIsInstallDirectory()
    {
        using var host = CliApplication.BuildHost(new CliOutput(TextWriter.Null, TextWriter.Null), verbose: false);
        var environment = host.Services.GetRequiredService<IHostEnvironment>();

        Assert.Equal("myrpa", environment.ApplicationName);
        Assert.Equal(typeof(CliApplication).Assembly.GetName().Name, environment.ApplicationName);
        Assert.Equal(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), Path.TrimEndingDirectorySeparator(environment.ContentRootPath));
    }

    [Fact]
    public async Task Info_PrintsFoundationFacts_AndRegisteredActivities()
    {
        var result = await Cli.RunAsync("info");

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.StartsWith("MyRPA ", result.Out, StringComparison.Ordinal);
        Assert.Contains("Workflow schema version: 1.0", result.Out, StringComparison.Ordinal);
        Assert.Contains($"Activity source: {DiagnosticNames.RuntimeActivitySource}", result.Out, StringComparison.Ordinal);
        Assert.Contains("Registered activity types: 12", result.Out, StringComparison.Ordinal);
        Assert.Contains("Core.InvokeWorkflow", result.Out, StringComparison.Ordinal);
        Assert.Matches(@"Execution ID: [0-9a-f]{32}", result.Out);
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

        var result = await Cli.RunAsync("info");

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        var executionId = result.Out.Split('\n').Single(l => l.StartsWith("Execution ID: ", StringComparison.Ordinal))["Execution ID: ".Length..].Trim();
        Assert.Contains(stopped, a => a.OperationName == InfoCommand.OperationName
            && Equals(a.GetTagItem(DiagnosticNames.ExecutionIdKey), executionId)
            && Equals(a.GetTagItem(DiagnosticNames.OutcomeKey), "Succeeded"));
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("version")]
    public async Task Version_PrintsVersion(string arg)
    {
        var result = await Cli.RunAsync(arg);

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.StartsWith("MyRPA 0.1.0", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_ListsCommandsUsageAndExitCodes()
    {
        var result = await Cli.RunAsync("--help");

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Contains("myrpa run <workflow.json>", result.Out, StringComparison.Ordinal);
        Assert.Contains("myrpa validate <workflow.json>", result.Out, StringComparison.Ordinal);
        Assert.Contains("3 invalid workflow, 4 timed out", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoArguments_ReturnsUsage()
    {
        var result = await Cli.RunAsync();

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Empty(result.Out);
        Assert.Contains("Usage:", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCommand_ReturnsUsage_OnStandardError()
    {
        var result = await Cli.RunAsync("deploy");

        Assert.Equal(CliExitCodes.Usage, result.ExitCode);
        Assert.Empty(result.Out);
        Assert.Contains("Unknown command 'deploy'", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerboseOption_IsAcceptedBeforeOrAfterCommand()
    {
        Assert.Equal(CliExitCodes.Success, (await Cli.RunAsync("--verbose", "info")).ExitCode);
        Assert.Equal(CliExitCodes.Success, (await Cli.RunAsync("info", "--verbose")).ExitCode);
    }

    [Fact]
    public async Task CancelledToken_ReturnsCancelled()
    {
        var result = await Cli.RunAsync(new CancellationToken(canceled: true), "info");

        Assert.Equal(CliExitCodes.Cancelled, result.ExitCode);
    }
}
