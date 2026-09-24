namespace MyRPA.Core.Activities;

/// <summary>
/// Metadata describing an activity type, declared as data rather than UI attributes (Phase 0 finding D2).
/// Used by validation, the engine and tooling (CLI, Studio, AI workflow generation).
/// </summary>
public sealed record ActivityDescriptor
{
    /// <summary>Creates an activity descriptor.</summary>
    /// <param name="typeName">The registered activity type name.</param>
    /// <param name="displayName">Human-readable name.</param>
    /// <param name="category">Grouping used by tooling, e.g. "Control Flow" or "Browser".</param>
    /// <param name="description">Optional longer description.</param>
    /// <param name="properties">Accepted properties.</param>
    /// <param name="allowsChildren">Whether the node may have an ordered <c>children</c> list (e.g. <c>Core.Sequence</c>).</param>
    /// <param name="slots">Accepted named single-child slots.</param>
    public ActivityDescriptor(
        ActivityTypeName typeName,
        string displayName,
        string category,
        string? description = null,
        IEnumerable<ActivityPropertyDefinition>? properties = null,
        bool allowsChildren = false,
        IEnumerable<ActivitySlotDefinition>? slots = null)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        TypeName = typeName;
        DisplayName = displayName;
        Category = category;
        Description = description;
        Properties = [.. properties ?? []];
        AllowsChildren = allowsChildren;
        Slots = [.. slots ?? []];

        if (Properties.GroupBy(p => p.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } duplicateProperty)
        {
            throw new ArgumentException($"Property '{duplicateProperty.Key}' is declared more than once.", nameof(properties));
        }

        if (Slots.GroupBy(s => s.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } duplicateSlot)
        {
            throw new ArgumentException($"Slot '{duplicateSlot.Key}' is declared more than once.", nameof(slots));
        }

        foreach (var local in Properties.Where(p => p.Kind == ActivityPropertyKind.LocalName))
        {
            if (local.ScopeSlots.Any(s => !Slots.Any(d => d.Accepts(s) || d.Name == s)))
            {
                throw new ArgumentException($"Local '{local.Name}' refers to an undeclared slot.", nameof(properties));
            }
        }
    }

    /// <summary>The registered activity type name.</summary>
    public ActivityTypeName TypeName { get; }

    /// <summary>Human-readable name.</summary>
    public string DisplayName { get; }

    /// <summary>Grouping used by tooling.</summary>
    public string Category { get; }

    /// <summary>Optional longer description.</summary>
    public string? Description { get; }

    /// <summary>Accepted properties.</summary>
    public IReadOnlyList<ActivityPropertyDefinition> Properties { get; }

    /// <summary>Whether the node may have an ordered children list.</summary>
    public bool AllowsChildren { get; }

    /// <summary>Accepted named single-child slots.</summary>
    public IReadOnlyList<ActivitySlotDefinition> Slots { get; }

    /// <summary>Finds a property definition by name.</summary>
    /// <param name="name">Property name.</param>
    public ActivityPropertyDefinition? FindProperty(string name) =>
        Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>Finds the slot definition that accepts <paramref name="slotName"/>.</summary>
    /// <param name="slotName">Slot name used in a workflow.</param>
    public ActivitySlotDefinition? FindSlot(string slotName) => Slots.FirstOrDefault(s => s.Accepts(slotName));
}
