namespace MyRPA.Core.Activities;

/// <summary>
/// The kind of value a property expects (catalog 1.2, ADR-0042). Metadata for tools (a Studio, a future assistant): the
/// engine still converts and checks values at run time, as the activity does.
/// </summary>
#pragma warning disable CA1720 // Member names are the workflow type names (PRD 2.3), not CLR types.
public enum ActivityValueType
{
    /// <summary>Any value (the default when nothing is declared).</summary>
    Any = 0,

    /// <summary>Text.</summary>
    String = 1,

    /// <summary>A whole number.</summary>
    Int = 2,

    /// <summary>A decimal number.</summary>
    Decimal = 3,

    /// <summary>true or false.</summary>
    Boolean = 4,

    /// <summary>A date and time with offset.</summary>
    DateTime = 5,

    /// <summary>A list of values.</summary>
    List = 6,

    /// <summary>A dictionary of values by text keys.</summary>
    Dictionary = 7,
}
#pragma warning restore CA1720
