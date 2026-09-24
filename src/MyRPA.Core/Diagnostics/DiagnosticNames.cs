namespace MyRPA.Core.Diagnostics;

/// <summary>
/// Names shared by logging scopes and tracing (<see cref="System.Diagnostics.ActivitySource"/>) so that logs and
/// spans can be correlated. These names are a public contract (ADR-0006, ADR-0010); change them only through an ADR.
/// </summary>
public static class DiagnosticNames
{
    /// <summary>Name of the <see cref="System.Diagnostics.ActivitySource"/> used by the MyRPA runtime.</summary>
    public const string RuntimeActivitySource = "MyRPA.Runtime";

    /// <summary>Log category used for messages written by workflows (the <c>Core.Log</c> activity).</summary>
    public const string WorkflowLogCategory = "MyRPA.Workflow.Log";

    /// <summary>Span name of one workflow execution.</summary>
    public const string WorkflowExecuteOperation = "workflow.execute";

    /// <summary>Log scope key and span tag holding the <see cref="Identifiers.ExecutionId"/>.</summary>
    public const string ExecutionIdKey = "myrpa.execution.id";

    /// <summary>Log scope key and span tag holding the invoking execution's id for nested workflows.</summary>
    public const string ParentExecutionIdKey = "myrpa.parent_execution.id";

    /// <summary>Log scope key and span tag holding the <see cref="Identifiers.WorkflowId"/>.</summary>
    public const string WorkflowIdKey = "myrpa.workflow.id";

    /// <summary>Log scope key and span tag holding the <see cref="Identifiers.NodeId"/>.</summary>
    public const string NodeIdKey = "myrpa.node.id";

    /// <summary>Log scope key and span tag holding the <see cref="Identifiers.CorrelationId"/>.</summary>
    public const string CorrelationIdKey = "myrpa.correlation.id";

    /// <summary>Span tag holding the activity type name of a node span.</summary>
    public const string ActivityTypeKey = "myrpa.activity.type";

    /// <summary>Span tag holding the <see cref="Execution.ExecutionStatus"/> of a completed scope.</summary>
    public const string OutcomeKey = "myrpa.outcome";
}
