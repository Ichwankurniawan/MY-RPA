using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace MyRPA.Sdk.Plugins;

/// <summary>
/// A plugin's own version: a Semantic Versioning 2.0 subset, <c>MAJOR.MINOR.PATCH</c> with an optional
/// <c>-prerelease</c> label of dot-separated ASCII letters, digits and hyphens (no build metadata). Pre-release versions
/// sort before the release with the same numbers; labels are compared ordinally.
/// </summary>
public sealed class PluginVersion : IEquatable<PluginVersion>, IComparable<PluginVersion>
{
    private PluginVersion(int major, int minor, int patch, string? prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    /// <summary>Major version (incompatible changes).</summary>
    public int Major { get; }

    /// <summary>Minor version (compatible additions).</summary>
    public int Minor { get; }

    /// <summary>Patch version (compatible fixes).</summary>
    public int Patch { get; }

    /// <summary>Pre-release label, or <see langword="null"/> for a release.</summary>
    public string? Prerelease { get; }

    /// <summary>Parses a version.</summary>
    /// <param name="value">Text such as <c>1.2.0</c> or <c>2.0.0-beta.1</c>.</param>
    /// <exception cref="FormatException">The text is not a valid version.</exception>
    public static PluginVersion Parse(string value) =>
        TryParse(value, out var version) ? version : throw new FormatException($"'{value}' is not a valid plugin version (MAJOR.MINOR.PATCH[-prerelease]).");

    /// <summary>Tries to parse a version.</summary>
    /// <param name="value">Candidate text.</param>
    /// <param name="version">The version when valid.</param>
    public static bool TryParse(string? value, [NotNullWhen(true)] out PluginVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            return false;
        }

        var dash = value.IndexOf('-', StringComparison.Ordinal);
        var core = dash < 0 ? value : value[..dash];
        var prerelease = dash < 0 ? null : value[(dash + 1)..];
        var parts = core.Split('.');
        if (parts.Length != 3
            || !SdkVersion.TryParsePart(parts[0], out var major)
            || !SdkVersion.TryParsePart(parts[1], out var minor)
            || !SdkVersion.TryParsePart(parts[2], out var patch))
        {
            return false;
        }

        if (prerelease is not null
            && prerelease.Split('.').Any(label => label.Length == 0 || !label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            return false;
        }

        version = new PluginVersion(major, minor, patch, prerelease);
        return true;
    }

    /// <summary>
    /// Returns <see langword="true"/> when this version satisfies a dependency on <paramref name="minimum"/>: the same
    /// major version and not lower (caret semantics, as in <c>^1.2.0</c>).
    /// </summary>
    /// <param name="minimum">The minimum compatible version.</param>
    public bool IsCompatibleWith(PluginVersion minimum)
    {
        ArgumentNullException.ThrowIfNull(minimum);
        return Major == minimum.Major && CompareTo(minimum) >= 0;
    }

    /// <inheritdoc />
    public int CompareTo(PluginVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var byNumbers = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (byNumbers != 0)
        {
            return byNumbers;
        }

        return (Prerelease, other.Prerelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(Prerelease, other.Prerelease),
        };
    }

    /// <inheritdoc />
    public bool Equals(PluginVersion? other) => CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PluginVersion);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}") + (Prerelease is null ? string.Empty : "-" + Prerelease);

    /// <summary>Compares two versions.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator <(PluginVersion? left, PluginVersion? right) => Compare(left, right) < 0;

    /// <summary>Compares two versions.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator >(PluginVersion? left, PluginVersion? right) => Compare(left, right) > 0;

    /// <summary>Compares two versions.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator <=(PluginVersion? left, PluginVersion? right) => Compare(left, right) <= 0;

    /// <summary>Compares two versions.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator >=(PluginVersion? left, PluginVersion? right) => Compare(left, right) >= 0;

    /// <summary>Tests equality.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator ==(PluginVersion? left, PluginVersion? right) => Compare(left, right) == 0;

    /// <summary>Tests inequality.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator !=(PluginVersion? left, PluginVersion? right) => Compare(left, right) != 0;

    private static int Compare(PluginVersion? left, PluginVersion? right) =>
        left is null ? (right is null ? 0 : -1) : left.CompareTo(right);
}
