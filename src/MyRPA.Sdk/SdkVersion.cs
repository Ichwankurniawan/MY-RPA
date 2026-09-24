using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace MyRPA.Sdk;

/// <summary>
/// Version of the Automation SDK contract, <c>major.minor</c> (ADR-0013). A plugin built for SDK <c>M.m</c> runs on a host
/// whose SDK has the same major version and a minor version of at least <c>m</c>: minor versions only add members,
/// major versions may break plugins.
/// </summary>
public sealed record SdkVersion
{
    /// <summary>Creates a version.</summary>
    /// <param name="major">Major version (breaking changes).</param>
    /// <param name="minor">Minor version (additive changes).</param>
    public SdkVersion(int major, int minor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        Major = major;
        Minor = minor;
    }

    /// <summary>Major version.</summary>
    public int Major { get; }

    /// <summary>Minor version.</summary>
    public int Minor { get; }

    /// <summary>Parses <c>major.minor</c>.</summary>
    /// <param name="value">Text such as <c>1.0</c>.</param>
    /// <param name="version">The parsed version.</param>
    public static bool TryParse(string? value, [NotNullWhen(true)] out SdkVersion? version)
    {
        version = null;
        var parts = value?.Split('.');
        if (parts is not { Length: 2 } || !TryParsePart(parts[0], out var major) || !TryParsePart(parts[1], out var minor))
        {
            return false;
        }

        version = new SdkVersion(major, minor);
        return true;
    }

    /// <summary>Returns <see langword="true"/> when a plugin that requires <paramref name="required"/> can run on this version.</summary>
    /// <param name="required">The SDK version the plugin was built for.</param>
    public bool Supports(SdkVersion required)
    {
        ArgumentNullException.ThrowIfNull(required);
        return required.Major == Major && required.Minor <= Minor;
    }

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");

    internal static bool TryParsePart(string part, out int value)
    {
        value = 0;
        return part.Length is > 0 and <= 6
            && part.All(char.IsAsciiDigit)
            && (part.Length == 1 || part[0] != '0')
            && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
