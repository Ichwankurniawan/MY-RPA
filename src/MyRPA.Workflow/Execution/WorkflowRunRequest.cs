using MyRPA.Core.Identifiers;

namespace MyRPA.Workflow.Execution;

/// <summary>Input for <see cref="IWorkflowRunner.RunAsync"/>.</summary>
public sealed class WorkflowRunRequest
{
    /// <summary>Input argument values by name (host values are normalized to canonical workflow values).</summary>
    public IReadOnlyDictionary<string, object?> Arguments { get; init; } = new Dictionary<string, object?>();

    /// <summary>Correlation supplied by the caller; generated when <see langword="null"/>.</summary>
    public CorrelationId? CorrelationId { get; init; }

    /// <summary>Maximum run time; <see langword="null"/> uses <c>WorkflowRuntimeOptions.DefaultTimeout</c>.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Where the workflow was loaded from (e.g. a full file path). Used to resolve <c>Core.InvokeWorkflow</c> references
    /// and as the confinement root for them (ADR-0012). <see langword="null"/> disables relative invocation.
    /// </summary>
    public string? Location { get; init; }
}
