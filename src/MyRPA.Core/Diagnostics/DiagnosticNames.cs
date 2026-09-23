namespace MyRPA.Core.Diagnostics;

/// <summary>
/// Names shared by logging scopes and tracing (<see cref="System.Diagnostics.ActivitySource"/>) so that logs and
/// spans can be correlated. These names are a public contract (ADR-0006); change them only through an ADR.
/// </summary>
public static class DiagnosticNames
{
    /// <summary>Name of the <see cref="System.Diagnostics.ActivitySource"/> used by the MyRPA runtime.</summary>
    public const string RuntimeActivitySource = "MyRPA.Runtime";

    /// <summary>Log scope key and span tag holding the <see cref="Identifiers.ExecutionId"/>.</summary>
    public const string ExecutionIdKey = "myrpa.execution.id";

    /// <summary>Log scope key and span tag holding the <see cref="Identifiers.WorkflowId"/>.</summary>
    public const string WorkflowIdKey = "myrpa.workflow.id";

    /// <summary>Log scope key and span tag holding the <see cref="Identifiers.NodeId"/>.</summary>
    public const string NodeIdKey = "myrpa.node.id";

    /// <summary>Log scope key and span tag holding the <see cref="Identifiers.CorrelationId"/>.</summary>
    public const string CorrelationIdKey = "myrpa.correlation.id";
}
