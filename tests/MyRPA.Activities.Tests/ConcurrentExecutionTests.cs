using Microsoft.Extensions.DependencyInjection;
using MyRPA.Core.Execution;
using MyRPA.Runtime;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Validation;

namespace MyRPA.Activities.Tests;

/// <summary>
/// Execution isolation (Phase 1 M3, Phase 2 review): one runner and one definition serve many simultaneous runs
/// without sharing variables, arguments, cancellation or identity.
/// </summary>
public sealed class ConcurrentExecutionTests
{
    private const int Runs = 400;

    private const string SumWorkflow = """
        { "schemaVersion": "1.0", "id": "sum", "name": "Sum", "version": "1.0.0",
          "arguments": [ { "name": "n", "direction": "In", "type": "Int", "required": true },
                         { "name": "total", "direction": "Out", "type": "Int" } ],
          "variables": [ { "name": "i", "type": "Int", "default": 0 }, { "name": "acc", "type": "Int", "default": 0 } ],
          "root": { "id": "r", "type": "Core.Sequence", "children": [
            { "id": "w", "type": "Core.While", "properties": { "condition": "i < n" }, "slots": { "body":
              { "id": "b", "type": "Core.Sequence", "children": [
                { "id": "inc", "type": "Core.Assign", "properties": { "to": "i", "value": "i + 1" } },
                { "id": "yield", "type": "Core.Delay", "properties": { "milliseconds": 0 } },
                { "id": "add", "type": "Core.Assign", "properties": { "to": "acc", "value": "acc + i" } } ] } } },
            { "id": "out", "type": "Core.Assign", "properties": { "to": "total", "value": "acc" } } ] } }
        """;

    [Fact]
    public async Task ManySimultaneousRuns_OfOneDefinition_AreIsolated()
    {
        await using var services = new ServiceCollection().AddLogging().AddMyRpaRuntime().AddMyRpaActivities()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var workflow = services.GetRequiredService<WorkflowLoader>().Load(SumWorkflow).Workflow!;
        var runner = services.GetRequiredService<IWorkflowRunner>();
        var token = TestContext.Current.CancellationToken;

        var results = await Task.WhenAll(Enumerable.Range(1, Runs).Select(n => Task.Run(
            async () => (N: n, Result: await runner.RunAsync(workflow, new WorkflowRunRequest { Arguments = new Dictionary<string, object?> { ["n"] = (long)n } }, token)),
            token)));

        Assert.All(results, r =>
        {
            Assert.Equal(ExecutionStatus.Succeeded, r.Result.Status);
            Assert.Equal((long)r.N * (r.N + 1) / 2, r.Result.Outputs["total"]);
        });
        Assert.Equal(Runs, results.Select(r => r.Result.ExecutionId).Distinct().Count());
        Assert.Equal(Runs, results.Select(r => r.Result.CorrelationId).Distinct().Count());
    }

    [Fact]
    public async Task CancellingOneRun_DoesNotAffectTheOthers()
    {
        await using var services = new ServiceCollection().AddLogging().AddMyRpaRuntime().AddMyRpaActivities()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var workflow = services.GetRequiredService<WorkflowLoader>().Load(SumWorkflow).Workflow!;
        var runner = services.GetRequiredService<IWorkflowRunner>();
        using var cancelOne = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancelOne.CancelAsync();

        var cancelled = runner.RunAsync(workflow, new WorkflowRunRequest { Arguments = new Dictionary<string, object?> { ["n"] = 1000L } }, cancelOne.Token);
        var others = Enumerable.Range(1, 20).Select(n =>
            runner.RunAsync(workflow, new WorkflowRunRequest { Arguments = new Dictionary<string, object?> { ["n"] = (long)n } }, TestContext.Current.CancellationToken)).ToList();

        Assert.Equal(ExecutionStatus.Cancelled, (await cancelled).Status);
        Assert.All(await Task.WhenAll(others), r => Assert.Equal(ExecutionStatus.Succeeded, r.Status));
    }
}
