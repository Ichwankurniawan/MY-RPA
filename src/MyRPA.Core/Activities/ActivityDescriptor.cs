namespace MyRPA.Core.Activities;

/// <summary>
/// Metadata describing an activity type, declared as data rather than UI attributes (Phase 0 finding D2).
/// Used by tooling (CLI, Studio, AI workflow generation) to present and validate activity usage.
/// </summary>
/// <remarks>
/// Phase 1 contains identity and presentation metadata only. The execution contract and property schema are
/// designed in Phase 2 together with the workflow engine.
/// </remarks>
public sealed record ActivityDescriptor
{
    /// <summary>Creates an activity descriptor.</summary>
    /// <param name="typeName">The registered activity type name.</param>
    /// <param name="displayName">Human-readable name.</param>
    /// <param name="category">Grouping used by tooling, e.g. "Control Flow" or "Browser".</param>
    /// <param name="description">Optional longer description.</param>
    public ActivityDescriptor(ActivityTypeName typeName, string displayName, string category, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        TypeName = typeName;
        DisplayName = displayName;
        Category = category;
        Description = description;
    }

    /// <summary>The registered activity type name.</summary>
    public ActivityTypeName TypeName { get; }

    /// <summary>Human-readable name.</summary>
    public string DisplayName { get; }

    /// <summary>Grouping used by tooling.</summary>
    public string Category { get; }

    /// <summary>Optional longer description.</summary>
    public string? Description { get; }
}
