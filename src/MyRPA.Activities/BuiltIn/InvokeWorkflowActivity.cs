using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;

namespace MyRPA.Activities.BuiltIn;

/// <summary>
/// <c>Core.InvokeWorkflow</c>: runs another workflow as a nested execution (ADR-0012). <c>arguments</c> maps child
/// In/InOut argument names to expressions; <c>outputs</c> maps child Out/InOut argument names to assignment targets in
/// this workflow. A child that does not succeed fails this node.
/// </summary>
public sealed class InvokeWorkflowActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("InvokeWorkflow"),
        "Invoke Workflow",
        CoreActivities.WorkflowCategory,
        "Runs another workflow and maps its arguments.",
        [
            new("workflow", ActivityPropertyKind.Text, isRequired: true, "Relative path of the workflow file."),
            new("arguments", ActivityPropertyKind.ExpressionMap, isRequired: false, "Child argument name to expression."),
            new("outputs", ActivityPropertyKind.AssignmentTargetMap, isRequired: false, "Child output argument name to target in this workflow."),
            new("timeoutMilliseconds", ActivityPropertyKind.Expression, isRequired: false, "Optional timeout for the child (Int)."),
        ]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var reference = context.GetText("workflow");
        var arguments = context.HasProperty("arguments") ? context.EvaluateMap("arguments") : new Dictionary<string, object?>();
        TimeSpan? timeout = null;
        if (context.HasProperty("timeoutMilliseconds"))
        {
            var milliseconds = context.EvaluateInt("timeoutMilliseconds");
            timeout = milliseconds > 0
                ? TimeSpan.FromMilliseconds(milliseconds)
                : throw new ArgumentOutOfRangeException(nameof(context), milliseconds, "timeoutMilliseconds must be positive.");
        }

        var result = await context.InvokeWorkflowAsync(reference, arguments, timeout).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var error = result.Error!;
            var where = error.NodeId is null ? string.Empty : $" at node '{error.NodeId}'";
            throw new WorkflowInvocationException(
                ExecutionErrorCodes.InvokedWorkflowFailed,
                $"Invoked workflow '{result.WorkflowId}' {result.Status}{where}: {error.Message}",
                result);
        }

        if (context.HasProperty("outputs"))
        {
            foreach (var (childArgument, target) in context.GetNameMap("outputs"))
            {
                if (!result.Outputs.TryGetValue(childArgument, out var value))
                {
                    throw new InvalidOperationException($"Invoked workflow '{result.WorkflowId}' has no output argument '{childArgument}'.");
                }

                context.SetValue(target, value);
            }
        }

        return ActivityResult.Completed;
    }
}
