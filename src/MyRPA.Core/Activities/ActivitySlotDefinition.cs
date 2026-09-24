namespace MyRPA.Core.Activities;

/// <summary>
/// Declares a named single-child position of an activity, such as <c>then</c> of <c>Core.If</c> or <c>body</c> of
/// <c>Core.While</c>. A prefix slot (e.g. <c>case:</c>) accepts any slot name starting with <see cref="Name"/>.
/// </summary>
public sealed record ActivitySlotDefinition
{
    /// <summary>Creates a slot definition.</summary>
    /// <param name="name">Slot name, or the prefix when <paramref name="isPrefix"/> is set.</param>
    /// <param name="isRequired">Whether the slot must be filled (ignored for prefix slots).</param>
    /// <param name="isPrefix">Whether <paramref name="name"/> is a prefix accepting many slots.</param>
    /// <param name="description">Optional help text.</param>
    public ActivitySlotDefinition(string name, bool isRequired = false, bool isPrefix = false, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (isPrefix && isRequired)
        {
            throw new ArgumentException("Prefix slots cannot be required.", nameof(isRequired));
        }

        Name = name;
        IsRequired = isRequired;
        IsPrefix = isPrefix;
        Description = description;
    }

    /// <summary>Slot name or prefix.</summary>
    public string Name { get; }

    /// <summary>Whether the slot must be filled.</summary>
    public bool IsRequired { get; }

    /// <summary>Whether <see cref="Name"/> is a prefix.</summary>
    public bool IsPrefix { get; }

    /// <summary>Optional help text.</summary>
    public string? Description { get; }

    /// <summary>Returns <see langword="true"/> when <paramref name="slotName"/> is accepted by this definition.</summary>
    /// <param name="slotName">A slot name used in a workflow.</param>
    public bool Accepts(string slotName)
    {
        ArgumentNullException.ThrowIfNull(slotName);
        return IsPrefix
            ? slotName.Length > Name.Length && slotName.StartsWith(Name, StringComparison.Ordinal)
            : string.Equals(slotName, Name, StringComparison.Ordinal);
    }
}
