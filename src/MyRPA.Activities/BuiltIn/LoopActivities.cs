using System.Globalization;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;

namespace MyRPA.Activities.BuiltIn;

/// <summary><c>Core.While</c>: runs slot <c>body</c> while <c>condition</c> is true (checked before each iteration).</summary>
public sealed class WhileActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("While"),
        "While",
        CoreActivities.ControlFlowCategory,
        "Repeats the body while the condition is true.",
        LoopSupport.ConditionProperties,
        slots: [new("body", isRequired: true)]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var limit = LoopSupport.MaxIterations(context);
        var iterations = 0L;
        while (context.EvaluateBoolean("condition"))
        {
            LoopSupport.CountIteration(context, ref iterations, limit);
            await context.ExecuteAsync(context.Node.Slots["body"]).ConfigureAwait(false);
        }

        return ActivityResult.Completed;
    }
}

/// <summary><c>Core.DoWhile</c>: runs slot <c>body</c>, then repeats while <c>condition</c> is true.</summary>
public sealed class DoWhileActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("DoWhile"),
        "Do While",
        CoreActivities.ControlFlowCategory,
        "Runs the body once, then repeats while the condition is true.",
        LoopSupport.ConditionProperties,
        slots: [new("body", isRequired: true)]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var limit = LoopSupport.MaxIterations(context);
        var iterations = 0L;
        do
        {
            LoopSupport.CountIteration(context, ref iterations, limit);
            await context.ExecuteAsync(context.Node.Slots["body"]).ConfigureAwait(false);
        }
        while (context.EvaluateBoolean("condition"));

        return ActivityResult.Completed;
    }
}

/// <summary>
/// <c>Core.ForEach</c>: evaluates <c>items</c> (a List) once and runs slot <c>body</c> for each item, with the item
/// (and optionally its zero-based index) available as read-only locals.
/// </summary>
public sealed class ForEachActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("ForEach"),
        "For Each",
        CoreActivities.ControlFlowCategory,
        "Runs the body once per list item.",
        [
            new("items", ActivityPropertyKind.Expression, isRequired: true, "List to iterate."),
            new("itemVariable", ActivityPropertyKind.LocalName, isRequired: true, "Name of the current item inside the body.", scopeSlots: ["body"]),
            new("indexVariable", ActivityPropertyKind.LocalName, isRequired: false, "Name of the zero-based index inside the body.", scopeSlots: ["body"]),
        ],
        slots: [new("body", isRequired: true)]);

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var items = context.EvaluateList("items");
        var itemName = context.GetName("itemVariable");
        var indexName = context.HasProperty("indexVariable") ? context.GetName("indexVariable") : null;
        var body = context.Node.Slots["body"];

        for (var i = 0; i < items.Count; i++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var locals = new Dictionary<string, object?>(StringComparer.Ordinal) { [itemName] = items[i] };
            if (indexName is not null)
            {
                locals[indexName] = (long)i;
            }

            await context.ExecuteAsync(body, locals).ConfigureAwait(false);
        }

        return ActivityResult.Completed;
    }
}

/// <summary>Shared loop helpers.</summary>
internal static class LoopSupport
{
    public static ActivityPropertyDefinition[] ConditionProperties =>
    [
        new("condition", ActivityPropertyKind.Expression, isRequired: true, "Boolean condition."),
        new("maxIterations", ActivityPropertyKind.Expression, isRequired: false, "Optional safety limit (Int); exceeding it fails the loop."),
    ];

    public static long? MaxIterations(IActivityContext context)
    {
        if (!context.HasProperty("maxIterations"))
        {
            return null;
        }

        var limit = context.EvaluateInt("maxIterations");
        return limit >= 0 ? limit : throw new InvalidOperationException("maxIterations must not be negative.");
    }

    public static void CountIteration(IActivityContext context, ref long iterations, long? limit)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (++iterations > limit)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Loop exceeded maxIterations ({limit})."));
        }
    }
}
