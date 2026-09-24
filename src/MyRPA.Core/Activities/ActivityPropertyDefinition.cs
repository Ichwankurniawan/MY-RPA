namespace MyRPA.Core.Activities;

/// <summary>Declares one property an activity accepts. Pure metadata, used by validation and tooling.</summary>
public sealed record ActivityPropertyDefinition
{
    /// <summary>Creates a property definition.</summary>
    /// <param name="name">Property name as written in workflow files (camelCase).</param>
    /// <param name="kind">How the value is written and interpreted.</param>
    /// <param name="isRequired">Whether the property must be present.</param>
    /// <param name="description">Optional help text.</param>
    /// <param name="allowedValues">For <see cref="ActivityPropertyKind.Text"/>: permitted values (case-sensitive). Empty means any.</param>
    /// <param name="scopeSlots">For <see cref="ActivityPropertyKind.LocalName"/>: slots in which the local is visible.</param>
    public ActivityPropertyDefinition(
        string name,
        ActivityPropertyKind kind,
        bool isRequired = false,
        string? description = null,
        IEnumerable<string>? allowedValues = null,
        IEnumerable<string>? scopeSlots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Name = name;
        Kind = kind;
        IsRequired = isRequired;
        Description = description;
        AllowedValues = [.. allowedValues ?? []];
        ScopeSlots = [.. scopeSlots ?? []];

        if (ScopeSlots.Count > 0 && kind != ActivityPropertyKind.LocalName)
        {
            throw new ArgumentException("Scope slots are only valid for LocalName properties.", nameof(scopeSlots));
        }
    }

    /// <summary>Property name as written in workflow files.</summary>
    public string Name { get; }

    /// <summary>How the value is written and interpreted.</summary>
    public ActivityPropertyKind Kind { get; }

    /// <summary>Whether the property must be present.</summary>
    public bool IsRequired { get; }

    /// <summary>Optional help text.</summary>
    public string? Description { get; }

    /// <summary>Permitted values for text properties; empty means any.</summary>
    public IReadOnlyList<string> AllowedValues { get; }

    /// <summary>Slots in which a declared local name is visible.</summary>
    public IReadOnlyList<string> ScopeSlots { get; }
}
