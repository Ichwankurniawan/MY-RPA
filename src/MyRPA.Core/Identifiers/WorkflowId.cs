using System.Diagnostics.CodeAnalysis;

namespace MyRPA.Core.Identifiers;

/// <summary>Identifies a workflow definition (stable across versions of the same workflow).</summary>
public sealed record WorkflowId
{
    /// <summary>Creates a workflow identifier.</summary>
    /// <param name="value">Identifier text; see <see cref="Identifier"/> for the allowed format.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a valid identifier.</exception>
    public WorkflowId(string value) => Value = Identifier.EnsureValid(value, nameof(value));

    /// <summary>The identifier text.</summary>
    public string Value { get; }

    /// <summary>Tries to create a workflow identifier without throwing.</summary>
    /// <param name="value">Identifier text.</param>
    /// <param name="result">The identifier when valid.</param>
    public static bool TryCreate(string? value, [NotNullWhen(true)] out WorkflowId? result)
    {
        result = Identifier.IsValid(value) ? new WorkflowId(value!) : null;
        return result is not null;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
