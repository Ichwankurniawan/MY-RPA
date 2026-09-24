using MyRPA.Core.Diagnostics;
using MyRPA.Workflow;
using MyRPA.Workflow.Execution;

namespace MyRPA.Runtime.Execution;

/// <summary>The engine's <see cref="IActivityContext"/> for one node execution.</summary>
internal sealed class ActivityContext(
    WorkflowRunner runner,
    ExecutionFrame frame,
    NodeDefinition node,
    ExecutionIdentity identity,
    VariableScope variables,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) : IActivityContext
{
    public NodeDefinition Node { get; } = node;

    public ExecutionIdentity Identity { get; } = identity;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    public TimeProvider TimeProvider { get; } = timeProvider;

    public DateTimeOffset? Deadline => frame.Deadline;

    public bool HasProperty(string propertyName) => Node.Properties.ContainsKey(propertyName);

    public object? Evaluate(string propertyName) => Property<ExpressionPropertyValue>(propertyName).Expression.Evaluate(variables);

    public IReadOnlyDictionary<string, object?> EvaluateMap(string propertyName) =>
        Property<ExpressionMapPropertyValue>(propertyName).Entries
            .ToDictionary(e => e.Key, e => e.Value.Evaluate(variables), StringComparer.Ordinal);

    public string GetText(string propertyName) => Property<TextPropertyValue>(propertyName).Text;

    public string GetName(string propertyName) => Property<NamePropertyValue>(propertyName).Name;

    public IReadOnlyDictionary<string, string> GetNameMap(string propertyName) => Property<NameMapPropertyValue>(propertyName).Entries;

    public void SetValue(string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IsDeclaredTarget(name))
        {
            throw new InvalidOperationException(
                $"Node '{Node.Id}' ({Node.Type}) cannot assign '{name}': only names declared by its assignment-target properties can be assigned.");
        }

        variables.Set(name, value);
    }

    public ValueTask ExecuteAsync(NodeDefinition node, IReadOnlyDictionary<string, object?>? locals = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!Node.Children.Contains(node) && !Node.Slots.Values.Contains(node))
        {
            throw new InvalidOperationException($"Node '{node.Id}' is not a child or slot of node '{Node.Id}'.");
        }

        var scope = locals is null || locals.Count == 0 ? variables : variables.CreateChild(locals);
        return runner.ExecuteNodeAsync(frame, node, scope, CancellationToken);
    }

    public ValueTask<WorkflowExecutionResult> InvokeWorkflowAsync(string reference, IReadOnlyDictionary<string, object?> arguments, TimeSpan? timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(arguments);
        return runner.InvokeAsync(frame, reference, arguments, timeout, CancellationToken);
    }

    // Validation guarantees name properties are either assignment targets (checked writable) or read-only locals, which
    // VariableScope refuses to assign; so every name property value is a legitimate target to offer here.
    private bool IsDeclaredTarget(string name) =>
        Node.Properties.Values.Any(p => p switch
        {
            NamePropertyValue single => string.Equals(single.Name, name, StringComparison.Ordinal),
            NameMapPropertyValue map => map.Entries.Values.Contains(name, StringComparer.Ordinal),
            _ => false,
        });

    private T Property<T>(string propertyName)
        where T : PropertyValue
    {
        ArgumentNullException.ThrowIfNull(propertyName);
        if (!Node.Properties.TryGetValue(propertyName, out var value))
        {
            throw new InvalidOperationException($"Node '{Node.Id}' has no property '{propertyName}'.");
        }

        return value as T ?? throw new InvalidOperationException(
            $"Property '{propertyName}' of node '{Node.Id}' is a {value.GetType().Name}, not a {typeof(T).Name}.");
    }
}
