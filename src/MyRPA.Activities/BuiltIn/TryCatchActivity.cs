using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;

namespace MyRPA.Activities.BuiltIn;

/// <summary>
/// <c>Core.TryCatch</c>: runs slot <c>try</c>; if a node inside fails, runs slot <c>catch</c> (optional) with the error
/// available as a Dictionary local (<c>message</c>, <c>code</c>, <c>nodeId</c>, <c>activityType</c>, <c>errorType</c>);
/// then runs slot <c>finally</c> (optional). Without <c>catch</c> the error propagates after <c>finally</c>.
/// Cancellation and timeouts are never caught, and <c>finally</c> does not run once the execution is cancelled.
/// </summary>
public sealed class TryCatchActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("TryCatch"),
        "Try Catch",
        CoreActivities.ControlFlowCategory,
        "Handles failures of the nodes in 'try'.",
        [new("exceptionVariable", ActivityPropertyKind.LocalName, isRequired: false, "Name of the error inside 'catch'.", scopeSlots: ["catch"])],
        slots: [new("try", isRequired: true), new("catch"), new("finally")]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var slots = context.Node.Slots;
        try
        {
            await context.ExecuteAsync(slots["try"]).ConfigureAwait(false);
        }
        catch (WorkflowActivityException ex) when (slots.ContainsKey("catch"))
        {
            IReadOnlyDictionary<string, object?>? locals = null;
            if (context.HasProperty("exceptionVariable"))
            {
                locals = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [context.GetName("exceptionVariable")] = WorkflowValues.Dictionary(
                    [
                        new("message", ex.ErrorMessage),
                        new("code", ex.Code),
                        new("nodeId", ex.NodeId.Value),
                        new("activityType", ex.ActivityType.Value),
                        new("errorType", ex.ErrorType),
                    ]),
                };
            }

            await context.ExecuteAsync(slots["catch"], locals).ConfigureAwait(false);
        }
        finally
        {
            if (slots.TryGetValue("finally", out var finallyNode) && !context.CancellationToken.IsCancellationRequested)
            {
                await context.ExecuteAsync(finallyNode).ConfigureAwait(false);
            }
        }

        return ActivityResult.Completed;
    }
}
