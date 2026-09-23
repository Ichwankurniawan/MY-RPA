using System.Diagnostics.CodeAnalysis;

namespace MyRPA.Core.Activities;

/// <summary>
/// The registered name of an activity type, e.g. <c>Core.Log</c> or <c>Browser.Click</c>.
/// Workflows reference activities by this name; it is resolved through <see cref="IActivityCatalog"/> and is
/// never interpreted as a CLR type name (ADR-0002, ADR-0008).
/// </summary>
/// <remarks>
/// Format: two or more dot-separated segments; each segment starts with an ASCII letter and continues with ASCII
/// letters or digits. The first segment is the namespace that owns the activity (avoids collisions between plugins).
/// </remarks>
public sealed record ActivityTypeName
{
    /// <summary>Maximum length of an activity type name.</summary>
    public const int MaxLength = 128;

    /// <summary>Creates an activity type name.</summary>
    /// <param name="value">Name such as <c>Core.Log</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="value"/> does not match the required format.</exception>
    public ActivityTypeName(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid activity type name. Expected 'Namespace.Name' (letters/digits, dot-separated).",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>The name text.</summary>
    public string Value { get; }

    /// <summary>The first segment, which identifies the owning namespace (for example <c>Core</c>).</summary>
    public string Namespace => Value[..Value.IndexOf('.', StringComparison.Ordinal)];

    /// <summary>Returns <see langword="true"/> when <paramref name="value"/> is a valid activity type name.</summary>
    /// <param name="value">Candidate name.</param>
    public static bool IsValid([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        var segments = value.Split('.');
        if (segments.Length < 2)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || !char.IsAsciiLetter(segment[0]))
            {
                return false;
            }

            foreach (var c in segment)
            {
                if (!char.IsAsciiLetterOrDigit(c))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Tries to create an activity type name without throwing.</summary>
    /// <param name="value">Candidate name.</param>
    /// <param name="result">The name when valid.</param>
    public static bool TryCreate(string? value, [NotNullWhen(true)] out ActivityTypeName? result)
    {
        result = IsValid(value) ? new ActivityTypeName(value) : null;
        return result is not null;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
