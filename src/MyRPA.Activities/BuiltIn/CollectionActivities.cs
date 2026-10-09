using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;
using static MyRPA.Activities.BuiltIn.BuiltInProperties;

namespace MyRPA.Activities.BuiltIn;

/// <summary>
/// Collection activities (Phase 7, ADR-0042) on Lists, including tables (a List of Dictionaries, as Csv.Read and
/// Excel.ReadRange return). No side effects; the input list is never changed.
/// </summary>
public static class CollectionActivities
{
    /// <summary>Category of the collection activities.</summary>
    public const string Category = "Collections";

    internal static string[] Operators => ["Equals", "NotEquals", "Contains", "GreaterThan", "LessThan", "IsNull", "IsNotNull"];

    internal static ActivityPropertyDefinition[] Condition(string what) =>
    [
        Input("items", ActivityValueType.List, "The list.", required: true),
        Input("key", ActivityValueType.String, "For a table (a List of Dictionaries): the column to compare. Omit it to compare the items themselves."),
        Choice("operator", $"How {what} is compared with value.", "Equals", Operators),
        Input("value", ActivityValueType.Any, "What to compare with (not used by IsNull and IsNotNull)."),
    ];

    /// <summary>Whether an item meets the condition of the node's key, operator and value.</summary>
    internal static Func<object?, bool> Predicate(IActivityContext context)
    {
        var key = OptionalText(context, "key");
        var op = context.GetTextOrDefault("operator", "Equals");
        var expected = context.HasProperty("value") ? context.Evaluate("value") : null;
        return item =>
        {
            var actual = Field(item, key);
            return op switch
            {
                "NotEquals" => !WorkflowValues.AreEqual(actual, expected),
                "Contains" => actual switch
                {
                    string s => expected is string part && s.Contains(part, StringComparison.Ordinal),
                    IReadOnlyDictionary<string, object?> d => expected is string k && d.ContainsKey(k),
                    IReadOnlyList<object?> l => l.Any(x => WorkflowValues.AreEqual(x, expected)),
                    _ => false,
                },
                "GreaterThan" => WorkflowValues.TryCompare(actual, expected, out var c) && c > 0,
                "LessThan" => WorkflowValues.TryCompare(actual, expected, out var c) && c < 0,
                "IsNull" => actual is null,
                "IsNotNull" => actual is not null,
                _ => WorkflowValues.AreEqual(actual, expected),
            };
        };
    }
}

/// <summary><c>Core.Collection.Sort</c>: a sorted copy of a list.</summary>
public sealed class CollectionSortActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Collection.Sort"),
        "Sort List",
        CollectionActivities.Category,
        "Sorts a list (numbers, text or dates), or a table by one column. Equal items keep their order; null comes first. Fails with InvalidInput when the values cannot be compared (for example text with numbers).",
        [
            Input("items", ActivityValueType.List, "The list.", required: true),
            Input("key", ActivityValueType.String, "For a table: the column to sort by."),
            Input("descending", ActivityValueType.Boolean, "Largest first.", defaultJson: "false"),
            Result(ActivityValueType.List, "Receives the sorted list."),
        ]);

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var key = OptionalText(context, "key");
        var items = List(context, "items");
        var keys = items.Select(i => Field(i, key)).ToList();
        // Checked first, so a failure is an ActivityFailedException and not an error inside the sort.
        var types = keys.Where(k => k is not null).Select(k => k is long or decimal ? "Number" : WorkflowValues.TypeNameOf(k)).Distinct().ToList();
        if (types.Count > 1 || keys.Any(k => k is not null and not (long or decimal or string or DateTimeOffset or bool)))
        {
            throw new ActivityFailedException("InvalidInput", $"'{context.Node.Id}' can sort numbers, text, dates or Booleans of one kind; it got {string.Join(", ", types)}.");
        }

        var comparer = Comparer<object?>.Create((a, b) =>
            a is null ? (b is null ? 0 : -1) : b is null ? 1 : WorkflowValues.TryCompare(a, b, out var c) ? c : 0);
        var order = Enumerable.Range(0, items.Count);
        var sorted = Flag(context, "descending", false) ? order.OrderByDescending(i => keys[i], comparer) : order.OrderBy(i => keys[i], comparer);
        SetResult(context, ToList([.. sorted.Select(i => items[i])]));
        return ActivityResult.CompletedTask;
    }
}

/// <summary><c>Core.Collection.Filter</c>: the items that meet a condition.</summary>
public sealed class CollectionFilterActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Collection.Filter"),
        "Filter List",
        CollectionActivities.Category,
        "Keeps the items (or table rows) that meet a condition, e.g. rows whose 'status' Equals 'open' or whose 'amount' is GreaterThan 100. Values that cannot be compared do not match.",
        [.. CollectionActivities.Condition("each item (or its column)"), Result(ActivityValueType.List, "Receives the matching items, in order.")]);

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var matches = CollectionActivities.Predicate(context);
        SetResult(context, ToList([.. List(context, "items").Where(matches)]));
        return ActivityResult.CompletedTask;
    }
}

/// <summary><c>Core.Collection.Find</c>: the first item that meets a condition.</summary>
public sealed class CollectionFindActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Collection.Find"),
        "Find in List",
        CollectionActivities.Category,
        "Finds the first item (or table row) that meets a condition, and its position.",
        [
            .. CollectionActivities.Condition("each item (or its column)"),
            Result(ActivityValueType.Any, "Receives the first match, or null when none."),
            new("index", ActivityPropertyKind.AssignmentTarget, isRequired: false, "Receives its position (0-based), or -1 when none.") { ValueType = ActivityValueType.Int },
        ]);

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var matches = CollectionActivities.Predicate(context);
        var items = List(context, "items");
        var index = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (matches(items[i]))
            {
                index = i;
                break;
            }
        }

        SetResult(context, index >= 0 ? items[index] : null);
        if (context.HasProperty("index"))
        {
            context.SetValue(context.GetName("index"), (long)index);
        }

        return ActivityResult.CompletedTask;
    }
}

/// <summary><c>Core.Collection.Merge</c>: two lists one after the other.</summary>
public sealed class CollectionMergeActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Collection.Merge"),
        "Merge Lists",
        CollectionActivities.Category,
        "Puts the items of a second list after the first; with distinct, an item equal to an earlier one is left out.",
        [
            Input("first", ActivityValueType.List, "The first list.", required: true),
            Input("second", ActivityValueType.List, "The list added after it.", required: true),
            Input("distinct", ActivityValueType.Boolean, "Leave out repeated items.", defaultJson: "false"),
            Result(ActivityValueType.List, "Receives the merged list."),
        ]);

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var merged = List(context, "first").Concat(List(context, "second"));
        if (Flag(context, "distinct", false))
        {
            var kept = new List<object?>();
            foreach (var item in merged)
            {
                if (!kept.Any(k => WorkflowValues.AreEqual(k, item)))
                {
                    kept.Add(item);
                }
            }

            merged = kept;
        }

        SetResult(context, ToList([.. merged]));
        return ActivityResult.CompletedTask;
    }
}
