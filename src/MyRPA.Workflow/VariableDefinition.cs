using MyRPA.Workflow.Values;

namespace MyRPA.Workflow;

/// <summary>Declares a workflow variable. Definition data only; runtime values live in the execution.</summary>
public sealed class VariableDefinition
{
    /// <summary>Creates a variable definition.</summary>
    /// <param name="name">Variable name (see <see cref="WorkflowNames"/>).</param>
    /// <param name="type">Declared type.</param>
    /// <param name="defaultValue">Canonical initial value.</param>
    public VariableDefinition(string name, WorkflowDataType type, object? defaultValue = null)
    {
        Name = WorkflowNames.EnsureValid(name, nameof(name));
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        if (!WorkflowValues.TryConvert(defaultValue, type, out var converted, out var error))
        {
            throw new ArgumentException($"Default value is invalid: {error}", nameof(defaultValue));
        }

        Type = type;
        DefaultValue = converted;
    }

    /// <summary>Variable name.</summary>
    public string Name { get; }

    /// <summary>Declared type.</summary>
    public WorkflowDataType Type { get; }

    /// <summary>Canonical initial value.</summary>
    public object? DefaultValue { get; }
}
