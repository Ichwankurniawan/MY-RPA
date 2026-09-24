using MyRPA.Workflow;
using MyRPA.Workflow.Expressions;
using MyRPA.Workflow.Values;

namespace MyRPA.Runtime.Execution;

/// <summary>
/// Runtime state of names for one execution: arguments and variables in the root scope, plus child scopes for
/// read-only locals (ForEach item, TryCatch exception). Runtime state only; never part of the definition.
/// </summary>
internal sealed class VariableScope : IExpressionScope
{
    private readonly VariableScope? _parent;
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    private VariableScope(VariableScope? parent, TimeProvider timeProvider)
    {
        _parent = parent;
        TimeProvider = timeProvider;
    }

    public TimeProvider TimeProvider { get; }

    public static VariableScope CreateRoot(WorkflowDefinition workflow, IReadOnlyDictionary<string, object?> argumentValues, TimeProvider timeProvider)
    {
        var scope = new VariableScope(null, timeProvider);
        foreach (var argument in workflow.Arguments)
        {
            argumentValues.TryGetValue(argument.Name, out var value);
            scope._slots.Add(argument.Name, new Slot(argument.Type, value, Writable: argument.Direction != ArgumentDirection.In, IsOutput: argument.IsOutput));
        }

        foreach (var variable in workflow.Variables)
        {
            scope._slots.Add(variable.Name, new Slot(variable.Type, variable.DefaultValue, Writable: true, IsOutput: false));
        }

        return scope;
    }

    public VariableScope CreateChild(IReadOnlyDictionary<string, object?> locals)
    {
        var child = new VariableScope(this, TimeProvider);
        foreach (var (name, value) in locals)
        {
            if (!WorkflowValues.TryNormalize(value, out var canonical, out var error))
            {
                throw new ArgumentException($"Local '{name}': {error}", nameof(locals));
            }

            child._slots.Add(name, new Slot(WorkflowDataType.Object, canonical, Writable: false, IsOutput: false));
        }

        return child;
    }

    public bool TryGetValue(string name, out object? value)
    {
        for (var scope = this; scope is not null; scope = scope._parent)
        {
            if (scope._slots.TryGetValue(name, out var slot))
            {
                value = slot.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    public void Set(string name, object? value)
    {
        for (var scope = this; scope is not null; scope = scope._parent)
        {
            if (!scope._slots.TryGetValue(name, out var slot))
            {
                continue;
            }

            if (!slot.Writable)
            {
                throw new InvalidOperationException($"'{name}' is read-only.");
            }

            if (!WorkflowValues.TryNormalize(value, out var canonical, out var normalizeError))
            {
                throw new WorkflowExpressionException($"Cannot assign '{name}': {normalizeError}");
            }

            if (!WorkflowValues.TryConvert(canonical, slot.Type, out var converted, out var error))
            {
                throw new WorkflowExpressionException($"Cannot assign '{name}': {error}");
            }

            scope._slots[name] = slot with { Value = converted };
            return;
        }

        throw new InvalidOperationException($"Unknown variable or argument '{name}'.");
    }

    public IReadOnlyDictionary<string, object?> GetOutputs() =>
        WorkflowValues.Dictionary(_slots.Where(s => s.Value.IsOutput).Select(s => new KeyValuePair<string, object?>(s.Key, s.Value.Value)));

    private sealed record Slot(WorkflowDataType Type, object? Value, bool Writable, bool IsOutput);
}
