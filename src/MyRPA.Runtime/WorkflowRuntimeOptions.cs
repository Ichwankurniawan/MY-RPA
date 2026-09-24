namespace MyRPA.Runtime;

/// <summary>Engine limits and defaults. Configured once by the composition root (<c>AddMyRpaRuntime</c>).</summary>
public sealed class WorkflowRuntimeOptions
{
    /// <summary>Maximum nesting of <c>Core.InvokeWorkflow</c> executions (ADR-0012). Default 10.</summary>
    public int MaxInvocationDepth { get; set; } = 10;

    /// <summary>Timeout applied when a run request specifies none. Default: no timeout.</summary>
    public TimeSpan? DefaultTimeout { get; set; }
}
