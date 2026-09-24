namespace MyRPA.Workflow.Validation;

/// <summary>Stable validation diagnostic codes (documented in docs/architecture/workflow-format.md).</summary>
public static class DiagnosticCodes
{
    /// <summary>The text is not valid JSON.</summary>
    public const string MalformedJson = "MYRPA1001";

    /// <summary>The JSON root is not an object.</summary>
    public const string RootNotObject = "MYRPA1002";

    /// <summary>A required field is missing.</summary>
    public const string MissingField = "MYRPA1003";

    /// <summary>A field has the wrong JSON type.</summary>
    public const string WrongFieldType = "MYRPA1004";

    /// <summary>A field is not part of the schema (warning).</summary>
    public const string UnknownField = "MYRPA1005";

    /// <summary>The schema version is missing or malformed.</summary>
    public const string InvalidSchemaVersion = "MYRPA1010";

    /// <summary>The schema version is not supported by this MyRPA version.</summary>
    public const string UnsupportedSchemaVersion = "MYRPA1011";

    /// <summary>The workflow id is invalid.</summary>
    public const string InvalidWorkflowId = "MYRPA1020";

    /// <summary>The workflow name or version is blank.</summary>
    public const string InvalidWorkflowText = "MYRPA1021";

    /// <summary>A node id is invalid.</summary>
    public const string InvalidNodeId = "MYRPA1030";

    /// <summary>A node id is used more than once.</summary>
    public const string DuplicateNodeId = "MYRPA1031";

    /// <summary>An activity type name is malformed.</summary>
    public const string InvalidActivityType = "MYRPA1032";

    /// <summary>An activity type is not registered.</summary>
    public const string UnknownActivityType = "MYRPA1033";

    /// <summary>A required activity property is missing.</summary>
    public const string MissingProperty = "MYRPA1040";

    /// <summary>A property is not accepted by the activity.</summary>
    public const string UnknownProperty = "MYRPA1041";

    /// <summary>A property value has the wrong shape for its kind.</summary>
    public const string InvalidPropertyValue = "MYRPA1042";

    /// <summary>An expression has a syntax error, unknown function or wrong argument count.</summary>
    public const string InvalidExpression = "MYRPA1043";

    /// <summary>An expression references an unknown name.</summary>
    public const string UnknownName = "MYRPA1044";

    /// <summary>An assignment target is unknown or read-only.</summary>
    public const string InvalidAssignmentTarget = "MYRPA1045";

    /// <summary>A text property has a value that is not allowed.</summary>
    public const string ValueNotAllowed = "MYRPA1046";

    /// <summary>A local name hides an existing name.</summary>
    public const string ShadowedName = "MYRPA1047";

    /// <summary>The activity does not accept a children list.</summary>
    public const string ChildrenNotAllowed = "MYRPA1050";

    /// <summary>The activity does not accept the slot.</summary>
    public const string UnknownSlot = "MYRPA1051";

    /// <summary>A required slot is empty.</summary>
    public const string MissingSlot = "MYRPA1052";

    /// <summary>An argument or variable name is invalid.</summary>
    public const string InvalidName = "MYRPA1060";

    /// <summary>An argument or variable name is declared more than once.</summary>
    public const string DuplicateName = "MYRPA1061";

    /// <summary>An argument direction is invalid.</summary>
    public const string InvalidDirection = "MYRPA1062";

    /// <summary>A data type name is invalid.</summary>
    public const string InvalidDataType = "MYRPA1063";

    /// <summary>A default value does not match the declared type.</summary>
    public const string InvalidDefault = "MYRPA1064";

    /// <summary>An Out argument declares a default or is marked required.</summary>
    public const string OutArgumentMisuse = "MYRPA1065";
}
