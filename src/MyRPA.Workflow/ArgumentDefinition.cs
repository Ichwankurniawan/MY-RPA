using MyRPA.Workflow.Values;

namespace MyRPA.Workflow;

/// <summary>Direction of a workflow argument (PRD 2.4).</summary>
public enum ArgumentDirection
{
    /// <summary>Supplied by the caller; read-only inside the workflow.</summary>
    In = 0,

    /// <summary>Produced by the workflow and returned to the caller.</summary>
    Out = 1,

    /// <summary>Supplied by the caller, assignable, and returned to the caller.</summary>
    InOut = 2,
}

/// <summary>Declares a workflow argument. Definition data only; runtime values live in the execution.</summary>
public sealed class ArgumentDefinition
{
    /// <summary>Creates an argument definition.</summary>
    /// <param name="name">Argument name (see <see cref="WorkflowNames"/>).</param>
    /// <param name="direction">Direction.</param>
    /// <param name="type">Declared type.</param>
    /// <param name="isRequired">Whether callers must supply a value (In/InOut only).</param>
    /// <param name="defaultValue">Canonical default used when the caller supplies none (In/InOut only).</param>
    public ArgumentDefinition(string name, ArgumentDirection direction, WorkflowDataType type, bool isRequired = false, object? defaultValue = null)
    {
        Name = WorkflowNames.EnsureValid(name, nameof(name));
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        if (direction == ArgumentDirection.Out && (isRequired || defaultValue is not null))
        {
            throw new ArgumentException("Out arguments cannot be required or have a default value.", nameof(direction));
        }

        if (!WorkflowValues.TryConvert(defaultValue, type, out var converted, out var error))
        {
            throw new ArgumentException($"Default value is invalid: {error}", nameof(defaultValue));
        }

        Direction = direction;
        Type = type;
        IsRequired = isRequired;
        DefaultValue = converted;
    }

    /// <summary>Argument name.</summary>
    public string Name { get; }

    /// <summary>Direction.</summary>
    public ArgumentDirection Direction { get; }

    /// <summary>Declared type.</summary>
    public WorkflowDataType Type { get; }

    /// <summary>Whether callers must supply a value.</summary>
    public bool IsRequired { get; }

    /// <summary>Canonical default value.</summary>
    public object? DefaultValue { get; }

    /// <summary>Whether callers can supply a value (In or InOut).</summary>
    public bool IsInput => Direction is ArgumentDirection.In or ArgumentDirection.InOut;

    /// <summary>Whether the value is returned to callers (Out or InOut).</summary>
    public bool IsOutput => Direction is ArgumentDirection.Out or ArgumentDirection.InOut;
}
