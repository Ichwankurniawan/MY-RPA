using System.Diagnostics.CodeAnalysis;

namespace MyRPA.Core.Activities;

/// <summary>
/// Read-only view of the activity types that were explicitly registered with the host.
/// There is no implicit discovery: an activity type exists only if a composition root registered it (ADR-0005).
/// </summary>
public interface IActivityCatalog
{
    /// <summary>All registered activity descriptors, ordered by type name.</summary>
    IReadOnlyList<ActivityDescriptor> Descriptors { get; }

    /// <summary>Looks up a registered activity type.</summary>
    /// <param name="typeName">The activity type name.</param>
    /// <param name="descriptor">The descriptor when registered.</param>
    /// <returns><see langword="true"/> when the type is registered.</returns>
    bool TryGet(ActivityTypeName typeName, [NotNullWhen(true)] out ActivityDescriptor? descriptor);
}
