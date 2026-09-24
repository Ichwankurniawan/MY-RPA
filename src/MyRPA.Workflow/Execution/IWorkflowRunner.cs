namespace MyRPA.Workflow.Execution;

/// <summary>
/// Executes validated workflow definitions. The single engine used by CLI, Studio, Robot and Orchestrator (PRD 7.3).
/// </summary>
public interface IWorkflowRunner
{
    /// <summary>
    /// Runs a workflow. Workflow failures, timeouts and cancellation are reported in the returned result
    /// (<see cref="WorkflowExecutionResult.Status"/>); the method throws only for invalid calls (null arguments).
    /// </summary>
    /// <param name="workflow">A validated workflow (from <see cref="Validation.WorkflowLoader"/> or constructed in code).</param>
    /// <param name="request">Arguments, correlation, timeout and location.</param>
    /// <param name="cancellationToken">Cancels the run (status <c>Cancelled</c>).</param>
    Task<WorkflowExecutionResult> RunAsync(WorkflowDefinition workflow, WorkflowRunRequest request, CancellationToken cancellationToken = default);
}
