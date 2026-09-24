namespace MyRPA.Workflow.Values;

/// <summary>Declared type of a workflow argument or variable (PRD 2.3). Every type also admits <see langword="null"/>.</summary>
#pragma warning disable CA1720 // Member names are the workflow type names used in workflow files (PRD 2.3), not CLR types.
public enum WorkflowDataType
{
    /// <summary>Text. Runtime representation: <see cref="string"/>.</summary>
    String = 0,

    /// <summary>64-bit integer. Runtime representation: <see cref="long"/>.</summary>
    Int = 1,

    /// <summary>Decimal number. Runtime representation: <see cref="decimal"/>.</summary>
    Decimal = 2,

    /// <summary>Boolean. Runtime representation: <see cref="bool"/>.</summary>
    Boolean = 3,

    /// <summary>Point in time with offset. Runtime representation: <see cref="DateTimeOffset"/>.</summary>
    DateTime = 4,

    /// <summary>Any workflow value.</summary>
    Object = 5,

    /// <summary>Ordered list of workflow values. Runtime representation: read-only list.</summary>
    List = 6,

    /// <summary>String-keyed map of workflow values. Runtime representation: read-only dictionary (ordinal keys).</summary>
    Dictionary = 7,
}
#pragma warning restore CA1720
