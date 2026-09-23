using MyRPA.Core.Identifiers;

namespace MyRPA.Core.Diagnostics;

/// <summary>
/// The identifiers that describe "which execution is this", carried into log scopes and trace tags.
/// Immutable; derive narrower identities with <see cref="ForWorkflow"/> and <see cref="ForNode"/>.
/// </summary>
public sealed record ExecutionIdentity
{
    /// <summary>Creates an execution identity.</summary>
    /// <param name="executionId">The execution being performed.</param>
    /// <param name="correlationId">Correlation across process boundaries.</param>
    /// <param name="workflowId">The workflow being executed, when known.</param>
    /// <param name="nodeId">The node currently executing, when known.</param>
    public ExecutionIdentity(
        ExecutionId executionId,
        CorrelationId correlationId,
        WorkflowId? workflowId = null,
        NodeId? nodeId = null)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        ArgumentNullException.ThrowIfNull(correlationId);
        ExecutionId = executionId;
        CorrelationId = correlationId;
        WorkflowId = workflowId;
        NodeId = nodeId;
    }

    /// <summary>The execution being performed.</summary>
    public ExecutionId ExecutionId { get; }

    /// <summary>Correlation across process boundaries.</summary>
    public CorrelationId CorrelationId { get; }

    /// <summary>The workflow being executed, when known.</summary>
    public WorkflowId? WorkflowId { get; }

    /// <summary>The node currently executing, when known.</summary>
    public NodeId? NodeId { get; }

    /// <summary>Creates an identity for a new execution.</summary>
    /// <param name="correlationId">Caller-supplied correlation; a new one is generated when <see langword="null"/>.</param>
    public static ExecutionIdentity CreateNew(CorrelationId? correlationId = null) =>
        new(ExecutionId.New(), correlationId ?? CorrelationId.New());

    /// <summary>Returns a copy scoped to <paramref name="workflowId"/>.</summary>
    /// <param name="workflowId">The workflow being executed.</param>
    public ExecutionIdentity ForWorkflow(WorkflowId workflowId)
    {
        ArgumentNullException.ThrowIfNull(workflowId);
        return new ExecutionIdentity(ExecutionId, CorrelationId, workflowId, NodeId);
    }

    /// <summary>Returns a copy scoped to <paramref name="nodeId"/>.</summary>
    /// <param name="nodeId">The node currently executing.</param>
    public ExecutionIdentity ForNode(NodeId nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        return new ExecutionIdentity(ExecutionId, CorrelationId, WorkflowId, nodeId);
    }

    /// <summary>
    /// Returns the identity as key/value pairs using <see cref="DiagnosticNames"/> keys. Only known values are included.
    /// Suitable for <c>ILogger.BeginScope</c> state and <see cref="System.Diagnostics.Activity"/> tags.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, object?>> ToTags()
    {
        var tags = new List<KeyValuePair<string, object?>>(4)
        {
            new(DiagnosticNames.ExecutionIdKey, ExecutionId.ToString()),
            new(DiagnosticNames.CorrelationIdKey, CorrelationId.Value),
        };

        if (WorkflowId is not null)
        {
            tags.Add(new(DiagnosticNames.WorkflowIdKey, WorkflowId.Value));
        }

        if (NodeId is not null)
        {
            tags.Add(new(DiagnosticNames.NodeIdKey, NodeId.Value));
        }

        return tags;
    }
}
