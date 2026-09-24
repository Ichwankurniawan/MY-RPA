namespace MyRPA.Workflow;

/// <summary>Rules for argument, variable and local names used in workflows and expressions.</summary>
public static class WorkflowNames
{
    /// <summary>Maximum name length.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="name"/> is a valid name: starts with an ASCII letter or '_',
    /// continues with ASCII letters, digits or '_', at most <see cref="MaxLength"/> characters, and is not a keyword
    /// (<c>true</c>, <c>false</c>, <c>null</c>).
    /// </summary>
    /// <param name="name">Candidate name.</param>
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength || name is "true" or "false" or "null")
        {
            return false;
        }

        if (!char.IsAsciiLetter(name[0]) && name[0] != '_')
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Throws when <paramref name="name"/> is not valid.</summary>
    /// <param name="name">Candidate name.</param>
    /// <param name="paramName">Parameter name for the exception.</param>
    public static string EnsureValid(string? name, string paramName) =>
        IsValid(name)
            ? name!
            : throw new ArgumentException($"'{name}' is not a valid name (letters, digits, '_'; must not start with a digit).", paramName);
}
