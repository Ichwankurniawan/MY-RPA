namespace MyRPA.Core.Execution;

/// <summary>Terminal outcome of a workflow execution or of an observed execution scope (ADR-0010).</summary>
public enum ExecutionStatus
{
    /// <summary>Completed without error.</summary>
    Succeeded = 0,

    /// <summary>An activity or the workflow failed; see the error details.</summary>
    Failed = 1,

    /// <summary>The caller cancelled the execution.</summary>
    Cancelled = 2,

    /// <summary>A configured timeout elapsed before completion.</summary>
    TimedOut = 3,
}
