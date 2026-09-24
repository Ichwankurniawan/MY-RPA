using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;

namespace MyRPA.Activities.BuiltIn;

/// <summary><c>Core.Assign</c>: evaluates <c>value</c> and stores it in the variable or Out/InOut argument <c>to</c>.</summary>
public sealed class AssignActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Assign"),
        "Assign",
        CoreActivities.DataCategory,
        "Assigns the value of an expression to a variable or Out/InOut argument.",
        [
            new("to", ActivityPropertyKind.AssignmentTarget, isRequired: true, "Variable or Out/InOut argument to assign."),
            new("value", ActivityPropertyKind.Expression, isRequired: true, "Value to assign."),
        ]);

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.SetValue(context.GetName("to"), context.Evaluate("value"));
        return ActivityResult.CompletedTask;
    }
}
