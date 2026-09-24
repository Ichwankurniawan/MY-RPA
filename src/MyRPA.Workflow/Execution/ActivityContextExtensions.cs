using MyRPA.Workflow.Expressions;
using MyRPA.Workflow.Values;

namespace MyRPA.Workflow.Execution;

/// <summary>Typed helpers for evaluating properties. Type mismatches raise <see cref="WorkflowExpressionException"/>.</summary>
public static class ActivityContextExtensions
{
    /// <summary>Evaluates a property that must be a Boolean.</summary>
    /// <param name="context">Activity context.</param>
    /// <param name="propertyName">Property name.</param>
    public static bool EvaluateBoolean(this IActivityContext context, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Evaluate(propertyName) is bool b ? b : throw Mismatch(context, propertyName, "Boolean");
    }

    /// <summary>Evaluates a property that must be an Int.</summary>
    /// <param name="context">Activity context.</param>
    /// <param name="propertyName">Property name.</param>
    public static long EvaluateInt(this IActivityContext context, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Evaluate(propertyName) is long l ? l : throw Mismatch(context, propertyName, "Int");
    }

    /// <summary>Evaluates a property that must be a List.</summary>
    /// <param name="context">Activity context.</param>
    /// <param name="propertyName">Property name.</param>
    public static IReadOnlyList<object?> EvaluateList(this IActivityContext context, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Evaluate(propertyName) is IReadOnlyList<object?> list and not IReadOnlyDictionary<string, object?>
            ? list
            : throw Mismatch(context, propertyName, "List");
    }

    /// <summary>Evaluates a property and formats it as text (any type).</summary>
    /// <param name="context">Activity context.</param>
    /// <param name="propertyName">Property name.</param>
    public static string EvaluateText(this IActivityContext context, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(context);
        return WorkflowValues.ToDisplayString(context.Evaluate(propertyName));
    }

    /// <summary>Returns a text property, or <paramref name="defaultValue"/> when absent.</summary>
    /// <param name="context">Activity context.</param>
    /// <param name="propertyName">Property name.</param>
    /// <param name="defaultValue">Value when the property is absent.</param>
    public static string GetTextOrDefault(this IActivityContext context, string propertyName, string defaultValue)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.HasProperty(propertyName) ? context.GetText(propertyName) : defaultValue;
    }

    private static WorkflowExpressionException Mismatch(IActivityContext context, string propertyName, string expected) =>
        new($"Property '{propertyName}' of node '{context.Node.Id}' must evaluate to {expected}.");
}
