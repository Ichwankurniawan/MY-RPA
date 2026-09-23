namespace MyRPA.Core.Identifiers;

/// <summary>
/// Validation rules shared by string-based identifiers (<see cref="WorkflowId"/>, <see cref="NodeId"/>,
/// <see cref="CorrelationId"/>).
/// </summary>
/// <remarks>
/// Identifiers appear in workflow files, logs, trace tags and (later) API routes, so they are restricted to a
/// conservative character set: ASCII letters, digits, '-', '_', '.' and ':'; 1 to <see cref="MaxLength"/> characters.
/// </remarks>
public static class Identifier
{
    /// <summary>Maximum identifier length.</summary>
    public const int MaxLength = 128;

    /// <summary>Returns <see langword="true"/> when <paramref name="value"/> is a valid identifier.</summary>
    /// <param name="value">Candidate identifier.</param>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Throws <see cref="ArgumentException"/> when <paramref name="value"/> is not a valid identifier.</summary>
    /// <param name="value">Candidate identifier.</param>
    /// <param name="paramName">Parameter name reported in the exception.</param>
    /// <returns>The validated value.</returns>
    public static string EnsureValid(string? value, string paramName)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid identifier. Use 1-{MaxLength} characters from [A-Za-z0-9-_.:].",
                paramName);
        }

        return value!;
    }
}
