using MyRPA.Core.Diagnostics;
using MyRPA.Core.Identifiers;
using MyRPA.Workflow.Values;

namespace MyRPA.Workflow.Execution;

/// <summary>
/// Optional per-run debug hook (ADR-0040), set with <see cref="WorkflowRunRequest.Debugger"/>. The engine awaits it before
/// every node starts, including graph steps and the nodes of invoked workflows; while the returned task is pending the
/// run is paused and its timeouts are suspended. Activities are never told they are being debugged.
/// </summary>
public interface IExecutionDebugger
{
    /// <summary>Called before <paramref name="at"/>'s node starts. Completing the task lets the node run.</summary>
    /// <param name="at">Where the run is.</param>
    /// <param name="cancellationToken">The run's token; a paused run that is cancelled ends as Cancelled.</param>
    /// <returns>Completes when the node may run.</returns>
    ValueTask BeforeNodeAsync(DebugStop at, CancellationToken cancellationToken);
}

/// <summary>Where a debugged run is about to execute a node.</summary>
public sealed class DebugStop
{
    private readonly Func<IReadOnlyList<DebugValue>> _readValues;

    /// <summary>Creates the stop (the engine does this).</summary>
    /// <param name="identity">The node's execution identity.</param>
    /// <param name="workflowId">The workflow the node belongs to.</param>
    /// <param name="node">The node about to start.</param>
    /// <param name="depth">
    /// Nesting depth in the whole run: the root node is 0, children and slots add 1, and an invoked workflow's root is one
    /// deeper than the invoking node.
    /// </param>
    /// <param name="readValues">Reads the arguments, variables and locals in scope.</param>
    public DebugStop(ExecutionIdentity identity, WorkflowId workflowId, NodeDefinition node, int depth, Func<IReadOnlyList<DebugValue>> readValues)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        WorkflowId = workflowId ?? throw new ArgumentNullException(nameof(workflowId));
        Node = node ?? throw new ArgumentNullException(nameof(node));
        ArgumentOutOfRangeException.ThrowIfNegative(depth);
        Depth = depth;
        _readValues = readValues ?? throw new ArgumentNullException(nameof(readValues));
    }

    /// <summary>The node's execution identity.</summary>
    public ExecutionIdentity Identity { get; }

    /// <summary>The workflow the node belongs to.</summary>
    public WorkflowId WorkflowId { get; }

    /// <summary>The node about to start.</summary>
    public NodeDefinition Node { get; }

    /// <summary>Nesting depth in the whole run (what step over and step out compare).</summary>
    public int Depth { get; }

    /// <summary>
    /// The arguments, variables and locals in scope, as canonical values. Read it during
    /// <see cref="IExecutionDebugger.BeforeNodeAsync"/> before awaiting anything: afterwards the run may have moved on.
    /// </summary>
    /// <returns>The names in scope, arguments first, then variables, then locals (innermost last).</returns>
    public IReadOnlyList<DebugValue> ReadValues() => _readValues();
}

/// <summary>What a <see cref="DebugValue"/> is.</summary>
public enum DebugValueKind
{
    /// <summary>A workflow argument.</summary>
    Argument = 0,

    /// <summary>A workflow variable.</summary>
    Variable = 1,

    /// <summary>A read-only local (a ForEach item, a caught exception).</summary>
    Local = 2,
}

/// <summary>One name in scope at a <see cref="DebugStop"/>, with its canonical value.</summary>
/// <param name="Name">The name.</param>
/// <param name="Kind">Argument, variable or local.</param>
/// <param name="Type">The declared type (<see cref="WorkflowDataType.Object"/> for locals).</param>
/// <param name="Value">The canonical value.</param>
public sealed record DebugValue(string Name, DebugValueKind Kind, WorkflowDataType Type, object? Value);
