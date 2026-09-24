namespace MyRPA.Core.Activities;

/// <summary>How a node property value is written in a workflow file and interpreted by the engine.</summary>
public enum ActivityPropertyKind
{
    /// <summary>
    /// An expression (ADR-0009). A JSON string is expression text; a JSON number, boolean or null is a literal.
    /// </summary>
    Expression = 0,

    /// <summary>A plain text value (never evaluated). May be restricted to <see cref="ActivityPropertyDefinition.AllowedValues"/>.</summary>
    Text = 1,

    /// <summary>The name of an existing, assignable variable or Out/InOut argument (an assignment target).</summary>
    AssignmentTarget = 2,

    /// <summary>
    /// Declares a new read-only local name that is visible only inside <see cref="ActivityPropertyDefinition.ScopeSlots"/>.
    /// </summary>
    LocalName = 3,

    /// <summary>A JSON object mapping names to expressions.</summary>
    ExpressionMap = 4,

    /// <summary>A JSON object mapping names to assignment targets.</summary>
    AssignmentTargetMap = 5,
}
