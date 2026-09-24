using System.Diagnostics.CodeAnalysis;

namespace MyRPA.Sdk.Plugins;

/// <summary>
/// Stable identity of a plugin, such as <c>MyRPA.Samples.Demo</c> or <c>Contoso.Browser</c>: two or more dot-separated
/// segments of ASCII letters/digits, each starting with a letter, at most <see cref="MaxLength"/> characters.
/// Ids are compared case-insensitively so that two plugins cannot differ only by case.
/// </summary>
public sealed class PluginId : IEquatable<PluginId>
{
    /// <summary>Maximum length of a plugin id.</summary>
    public const int MaxLength = 128;

    /// <summary>Creates a plugin id.</summary>
    /// <param name="value">Id text.</param>
    /// <exception cref="ArgumentException">The id is not valid.</exception>
    public PluginId(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a valid plugin id. Expected two or more dot-separated segments of ASCII letters/digits, such as 'Contoso.Browser'.",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>The id text as declared.</summary>
    public string Value { get; }

    /// <summary>Returns <see langword="true"/> when <paramref name="value"/> is a valid plugin id.</summary>
    /// <param name="value">Candidate.</param>
    public static bool IsValid([NotNullWhen(true)] string? value) => DottedName.IsValid(value, minimumSegments: 2, MaxLength);

    /// <summary>Tries to create a plugin id without throwing.</summary>
    /// <param name="value">Candidate.</param>
    /// <param name="id">The id when valid.</param>
    public static bool TryCreate(string? value, [NotNullWhen(true)] out PluginId? id)
    {
        id = IsValid(value) ? new PluginId(value) : null;
        return id is not null;
    }

    /// <inheritdoc />
    public bool Equals(PluginId? other) => other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PluginId);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Validation of dot-separated names (plugin ids, provider ids, selector strategies).</summary>
internal static class DottedName
{
    public static bool IsValid([NotNullWhen(true)] string? value, int minimumSegments, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
        {
            return false;
        }

        var segments = value.Split('.');
        return segments.Length >= minimumSegments
            && segments.All(s => s.Length > 0 && char.IsAsciiLetter(s[0]) && s.All(char.IsAsciiLetterOrDigit));
    }
}
