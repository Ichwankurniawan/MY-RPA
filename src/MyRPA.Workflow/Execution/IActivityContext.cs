using MyRPA.Core.Diagnostics;

namespace MyRPA.Workflow.Execution;

/// <summary>
/// Everything an activity may do while executing one node (ADR-0013, frozen for SDK 1.0). Implemented by the runtime;
/// activities never see engine internals, a service locator or global state. Services are constructor dependencies of
/// the activity, not members of the context. Logs written through an injected <c>ILogger</c> during
/// <see cref="IActivity.ExecuteAsync"/> are automatically correlated with <see cref="Identity"/>, and the engine traces
/// every node.
/// </summary>
public interface IActivityContext
{
    /// <summary>The node being executed.</summary>
    NodeDefinition Node { get; }

    /// <summary>Execution, correlation, workflow and node identity (for logs and traces).</summary>
    ExecutionIdentity Identity { get; }

    /// <summary>
    /// Cancelled when the execution is cancelled or its timeout elapses. Pass it to every awaited operation; when it is
    /// cancelled, stop promptly and let <see cref="OperationCanceledException"/> propagate. Cancellation is cooperative:
    /// the engine never abandons or aborts a running activity.
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// The earliest timeout of this execution and the executions that invoked it, or <see langword="null"/> when none is
    /// set. For technologies that take a timeout instead of a <see cref="System.Threading.CancellationToken"/>; the token is
    /// still authoritative.
    /// </summary>
    DateTimeOffset? Deadline { get; }

    /// <summary>The clock (use for delays and timestamps; never read the system clock directly).</summary>
    TimeProvider TimeProvider { get; }

    /// <summary>Whether the node has the property.</summary>
    /// <param name="propertyName">Property name.</param>
    bool HasProperty(string propertyName);

    /// <summary>Evaluates an expression property.</summary>
    /// <param name="propertyName">Property name.</param>
    /// <returns>A canonical workflow value.</returns>
    object? Evaluate(string propertyName);

    /// <summary>Evaluates every expression of an expression-map property.</summary>
    /// <param name="propertyName">Property name.</param>
    IReadOnlyDictionary<string, object?> EvaluateMap(string propertyName);

    /// <summary>Returns a text property.</summary>
    /// <param name="propertyName">Property name.</param>
    string GetText(string propertyName);

    /// <summary>Returns a name property (assignment target or local name).</summary>
    /// <param name="propertyName">Property name.</param>
    string GetName(string propertyName);

    /// <summary>Returns a name-map property (key to assignment target).</summary>
    /// <param name="propertyName">Property name.</param>
    IReadOnlyDictionary<string, string> GetNameMap(string propertyName);

    /// <summary>
    /// Assigns a variable or Out/InOut argument, converting to its declared type. Only names declared by this node's
    /// assignment-target properties (<see cref="GetName"/>, <see cref="GetNameMap"/> values) may be assigned, so the data
    /// flow of a workflow is visible without running it.
    /// </summary>
    /// <param name="name">Target name taken from an assignment-target property of <see cref="Node"/>.</param>
    /// <param name="value">Canonical value.</param>
    /// <exception cref="InvalidOperationException"><paramref name="name"/> is not a declared target of this node.</exception>
    void SetValue(string name, object? value);

    /// <summary>
    /// Executes a child or slot node of the current node, optionally with read-only locals visible to it.
    /// Exceptions from the child propagate (already attributed to the failing node).
    /// </summary>
    /// <param name="node">A node from <see cref="NodeDefinition.Children"/> or <see cref="NodeDefinition.Slots"/> of <see cref="Node"/>.</param>
    /// <param name="locals">Local names and canonical values.</param>
    ValueTask ExecuteAsync(NodeDefinition node, IReadOnlyDictionary<string, object?>? locals = null);

    /// <summary>
    /// Runs another workflow as a nested execution (ADR-0012). Returns its result; it does not throw for child
    /// failures, but throws <see cref="OperationCanceledException"/> when this execution is cancelled.
    /// </summary>
    /// <param name="reference">Workflow reference, resolved by the host's <see cref="IWorkflowResolver"/>.</param>
    /// <param name="arguments">Input argument values (canonical).</param>
    /// <param name="timeout">Optional timeout for the nested execution.</param>
    ValueTask<WorkflowExecutionResult> InvokeWorkflowAsync(string reference, IReadOnlyDictionary<string, object?> arguments, TimeSpan? timeout);
}
