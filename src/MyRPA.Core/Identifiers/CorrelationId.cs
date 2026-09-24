using System.Diagnostics.CodeAnalysis;

namespace MyRPA.Core.Identifiers;

/// <summary>
/// Correlates work across process boundaries (for example a CLI invocation, or later an orchestrator job and the
/// robot execution it causes). May be supplied by a caller or generated locally.
/// </summary>
public sealed record CorrelationId
{
    /// <summary>Creates a correlation identifier.</summary>
    /// <param name="value">Identifier text; see <see cref="Identifier"/> for the allowed format.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a valid identifier.</exception>
    public CorrelationId(string value) => Value = Identifier.EnsureValid(value, nameof(value));

    /// <summary>The identifier text.</summary>
    public string Value { get; }

    /// <summary>Tries to create a correlation identifier without throwing.</summary>
    /// <param name="value">Identifier text.</param>
    /// <param name="result">The identifier when valid.</param>
    public static bool TryCreate(string? value, [NotNullWhen(true)] out CorrelationId? result)
    {
        result = Identifier.IsValid(value) ? new CorrelationId(value!) : null;
        return result is not null;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
