using System.Diagnostics.CodeAnalysis;

namespace MyRPA.Core.Identifiers;

/// <summary>Identifies a node (an activity usage) inside a workflow definition; unique within that workflow.</summary>
public sealed record NodeId
{
    /// <summary>Creates a node identifier.</summary>
    /// <param name="value">Identifier text; see <see cref="Identifier"/> for the allowed format.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a valid identifier.</exception>
    public NodeId(string value) => Value = Identifier.EnsureValid(value, nameof(value));

    /// <summary>The identifier text.</summary>
    public string Value { get; }

    /// <summary>Tries to create a node identifier without throwing.</summary>
    /// <param name="value">Identifier text.</param>
    /// <param name="result">The identifier when valid.</param>
    public static bool TryCreate(string? value, [NotNullWhen(true)] out NodeId? result)
    {
        result = Identifier.IsValid(value) ? new NodeId(value!) : null;
        return result is not null;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
