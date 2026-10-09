using MyRPA.Workflow;
using MyRPA.Workflow.Execution;
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
            scope._slots.Add(argument.Name, new Slot(argument.Type, value, Writable: argument.Direction != ArgumentDirection.In, IsOutput: argument.IsOutput, DebugValueKind.Argument));
        }

        foreach (var variable in workflow.Variables)
        {
            scope._slots.Add(variable.Name, new Slot(variable.Type, variable.DefaultValue, Writable: true, IsOutput: false, DebugValueKind.Variable));
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

            child._slots.Add(name, new Slot(WorkflowDataType.Object, canonical, Writable: false, IsOutput: false, DebugValueKind.Local));
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

    /// <summary>What a debugger sees instead of a secret value.</summary>
    public const string MaskedValue = "••••";

    /// <summary>
    /// The names in scope for a debugger (ADR-0040): the root's arguments and variables in declaration order, then the
    /// locals from the outermost scope in. An inner name hides an outer one. Values of <paramref name="secretNames"/>
    /// are replaced by <see cref="MaskedValue"/> (ADR-0043).
    /// </summary>
    public IReadOnlyList<DebugValue> Snapshot(IReadOnlySet<string> secretNames)
    {
        var scopes = new List<VariableScope>();
        for (var scope = this; scope is not null; scope = scope._parent)
        {
            scopes.Insert(0, scope);
        }

        var values = new List<DebugValue>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var scope in scopes)
        {
            foreach (var (name, slot) in scope._slots)
            {
                var value = new DebugValue(name, slot.Kind, slot.Type, slot.Value is not null && secretNames.Contains(name) ? MaskedValue : slot.Value);
                if (positions.TryGetValue(name, out var position))
                {
                    values[position] = value;
                }
                else
                {
                    positions[name] = values.Count;
                    values.Add(value);
                }
            }
        }

        return values;
    }

    private sealed record Slot(WorkflowDataType Type, object? Value, bool Writable, bool IsOutput, DebugValueKind Kind);
}
