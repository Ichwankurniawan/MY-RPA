using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using MyRPA.Core.Activities;

namespace MyRPA.Activities;

/// <summary>
/// Immutable <see cref="IActivityCatalog"/> built from the <see cref="ActivityDescriptor"/> instances registered in
/// the service container. Duplicate type names are a configuration error.
/// </summary>
public sealed class ActivityCatalog : IActivityCatalog
{
    private readonly FrozenDictionary<ActivityTypeName, ActivityDescriptor> _byName;

    /// <summary>Creates the catalog.</summary>
    /// <param name="descriptors">Explicitly registered descriptors.</param>
    /// <exception cref="InvalidOperationException">Two descriptors share the same type name.</exception>
    public ActivityCatalog(IEnumerable<ActivityDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        var byName = new Dictionary<ActivityTypeName, ActivityDescriptor>();
        foreach (var descriptor in descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptor, nameof(descriptors));
            if (!byName.TryAdd(descriptor.TypeName, descriptor))
            {
                throw new InvalidOperationException(
                    $"Activity type '{descriptor.TypeName}' is registered more than once.");
            }
        }

        _byName = byName.ToFrozenDictionary();
        Descriptors = [.. byName.Values.OrderBy(d => d.TypeName.Value, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public IReadOnlyList<ActivityDescriptor> Descriptors { get; }

    /// <inheritdoc />
    public bool TryGet(ActivityTypeName typeName, [NotNullWhen(true)] out ActivityDescriptor? descriptor)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        return _byName.TryGetValue(typeName, out descriptor);
    }
}
