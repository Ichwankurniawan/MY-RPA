using MyRPA.Core.Activities;

namespace MyRPA.Activities.BuiltIn;

/// <summary>Names and categories of the built-in <c>Core.*</c> activities.</summary>
public static class CoreActivities
{
    /// <summary>Category for control-flow activities.</summary>
    public const string ControlFlowCategory = "Control Flow";

    /// <summary>Category for data activities.</summary>
    public const string DataCategory = "Data";

    /// <summary>Category for diagnostic activities.</summary>
    public const string DiagnosticsCategory = "Diagnostics";

    /// <summary>Category for workflow composition activities.</summary>
    public const string WorkflowCategory = "Workflow";

    internal static ActivityTypeName Name(string name) => new($"Core.{name}");
}
