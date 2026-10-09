using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;

namespace MyRPA.Activities.BuiltIn;

/// <summary>Shared property declarations and checks of the Phase 7 built-ins (ADR-0042): no side effects, one result.</summary>
internal static class BuiltInProperties
{
    /// <summary>An expression property with catalog 1.2 metadata.</summary>
    public static ActivityPropertyDefinition Input(string name, ActivityValueType type, string description, bool required = false, string? defaultJson = null) =>
        new(name, ActivityPropertyKind.Expression, required, description) { ValueType = type, DefaultValue = defaultJson };

    /// <summary>A choice among fixed values.</summary>
    public static ActivityPropertyDefinition Choice(string name, string description, string defaultValue, params string[] values) =>
        new(name, ActivityPropertyKind.Text, isRequired: false, description, values) { ValueType = ActivityValueType.String, DefaultValue = $"\"{defaultValue}\"" };

    /// <summary>The variable or Out/InOut argument that receives the result.</summary>
    public static ActivityPropertyDefinition Result(ActivityValueType type, string description) =>
        new("result", ActivityPropertyKind.AssignmentTarget, isRequired: true, description) { ValueType = type };

    public static string Text(IActivityContext context, string name) =>
        context.Evaluate(name) is string text ? text : throw Invalid(context, name, "text");

    public static string? OptionalText(IActivityContext context, string name) =>
        context.HasProperty(name) ? Text(context, name) : null;

    public static bool Flag(IActivityContext context, string name, bool defaultValue) =>
        !context.HasProperty(name) ? defaultValue : context.Evaluate(name) is bool b ? b : throw Invalid(context, name, "true or false");

    public static long Number(IActivityContext context, string name, long defaultValue) =>
        !context.HasProperty(name) ? defaultValue : context.Evaluate(name) is long l ? l : throw Invalid(context, name, "a whole number");

    public static IReadOnlyList<object?> List(IActivityContext context, string name) =>
        context.Evaluate(name) is IReadOnlyList<object?> list and not IReadOnlyDictionary<string, object?> ? list : throw Invalid(context, name, "a list");

    public static DateTimeOffset Date(IActivityContext context, string name) =>
        context.Evaluate(name) is DateTimeOffset value ? value : throw Invalid(context, name, "a date and time");

    public static void SetResult(IActivityContext context, object? value) => context.SetValue(context.GetName("result"), value);

    /// <summary>A value of the wrong kind: says which property and what it must be, never the value itself.</summary>
    public static ActivityFailedException Invalid(IActivityContext context, string name, string expected) =>
        new("InvalidInput", $"'{name}' of {context.Node.Type} '{context.Node.Id}' must be {expected}.");

    /// <summary>The time zone with this IANA (or Windows) id; UTC when <paramref name="id"/> is null.</summary>
    public static TimeZoneInfo Zone(string? id)
    {
        if (id is null || id is "UTC" or "Etc/UTC")
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ActivityFailedException("InvalidTimeZone", $"'{id}' is not a known time zone (use an IANA id such as Europe/Paris).", ex);
        }
    }

    /// <summary>A dictionary item's value at <paramref name="key"/> (null when missing), or the item itself without a key.</summary>
    public static object? Field(object? item, string? key) =>
        key is null ? item : item is IReadOnlyDictionary<string, object?> d && d.TryGetValue(key, out var value) ? value : null;

    public static IReadOnlyList<object?> ToList(IEnumerable<object?> items) => WorkflowValues.List(items);
}
