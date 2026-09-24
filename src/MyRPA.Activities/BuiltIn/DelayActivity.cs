using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;

namespace MyRPA.Activities.BuiltIn;

/// <summary><c>Core.Delay</c>: waits asynchronously using the injected clock; cancellable.</summary>
public sealed class DelayActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Delay"),
        "Delay",
        CoreActivities.ControlFlowCategory,
        "Waits for the given number of milliseconds without blocking a thread.",
        [new("milliseconds", ActivityPropertyKind.Expression, isRequired: true, "Duration in milliseconds (Int, 0 or more).")]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var milliseconds = context.EvaluateInt("milliseconds");
        if (milliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(context), milliseconds, "Delay milliseconds must not be negative.");
        }

        await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), context.TimeProvider, context.CancellationToken).ConfigureAwait(false);
        return ActivityResult.Completed;
    }
}
