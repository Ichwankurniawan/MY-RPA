using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace MyRPA.Workflow;

/// <summary>
/// Version of the workflow <em>file format</em> (not of an individual workflow's content).
/// A major increment means a breaking format change that requires a migration (PRD 10.6).
/// </summary>
public sealed record WorkflowSchemaVersion : IComparable<WorkflowSchemaVersion>
{
    /// <summary>Creates a schema version.</summary>
    /// <param name="major">Major version (at least 1).</param>
    /// <param name="minor">Minor version (at least 0).</param>
    public WorkflowSchemaVersion(int major, int minor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(major, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        Major = major;
        Minor = minor;
    }

    /// <summary>The schema version produced by this build of MyRPA.</summary>
    public static WorkflowSchemaVersion Current { get; } = new(1, 0);

    /// <summary>Major version.</summary>
    public int Major { get; }

    /// <summary>Minor version.</summary>
    public int Minor { get; }

    /// <summary>Parses "major.minor", e.g. "1.0".</summary>
    /// <param name="value">Text to parse.</param>
    /// <param name="result">The version when valid.</param>
    public static bool TryParse(string? value, [NotNullWhen(true)] out WorkflowSchemaVersion? result)
    {
        result = null;
        var parts = value?.Split('.');
        if (parts is not { Length: 2 }
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || major < 1)
        {
            return false;
        }

        result = new WorkflowSchemaVersion(major, minor);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(WorkflowSchemaVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var major = Major.CompareTo(other.Major);
        return major != 0 ? major : Minor.CompareTo(other.Minor);
    }

    /// <summary>Compares two versions.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator <(WorkflowSchemaVersion? left, WorkflowSchemaVersion? right) =>
        left is null ? right is not null : left.CompareTo(right) < 0;

    /// <summary>Compares two versions.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator >(WorkflowSchemaVersion? left, WorkflowSchemaVersion? right) =>
        left is not null && left.CompareTo(right) > 0;

    /// <summary>Compares two versions.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator <=(WorkflowSchemaVersion? left, WorkflowSchemaVersion? right) => !(left > right);

    /// <summary>Compares two versions.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator >=(WorkflowSchemaVersion? left, WorkflowSchemaVersion? right) => !(left < right);

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");
}
