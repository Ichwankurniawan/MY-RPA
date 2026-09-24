using System.Collections.ObjectModel;
using MyRPA.Workflow.Expressions;

namespace MyRPA.Workflow;

/// <summary>
/// A node property value, typed by the activity's <see cref="Core.Activities.ActivityPropertyKind"/>.
/// Expressions are stored parsed, so they are never re-parsed at run time.
/// </summary>
public abstract class PropertyValue
{
    private protected PropertyValue()
    {
    }
}

/// <summary>An expression property.</summary>
public sealed class ExpressionPropertyValue : PropertyValue
{
    /// <summary>Creates the value.</summary>
    /// <param name="expression">Parsed expression.</param>
    public ExpressionPropertyValue(WorkflowExpression expression) =>
        Expression = expression ?? throw new ArgumentNullException(nameof(expression));

    /// <summary>Parsed expression.</summary>
    public WorkflowExpression Expression { get; }
}

/// <summary>A plain text property (never evaluated).</summary>
public sealed class TextPropertyValue : PropertyValue
{
    /// <summary>Creates the value.</summary>
    /// <param name="text">Text.</param>
    public TextPropertyValue(string text) => Text = text ?? throw new ArgumentNullException(nameof(text));

    /// <summary>Text.</summary>
    public string Text { get; }
}

/// <summary>A name property: an assignment target or a declared local name.</summary>
public sealed class NamePropertyValue : PropertyValue
{
    /// <summary>Creates the value.</summary>
    /// <param name="name">Variable, argument or local name.</param>
    public NamePropertyValue(string name) => Name = WorkflowNames.EnsureValid(name, nameof(name));

    /// <summary>The name.</summary>
    public string Name { get; }
}

/// <summary>A map of keys to expressions.</summary>
public sealed class ExpressionMapPropertyValue : PropertyValue
{
    /// <summary>Creates the value.</summary>
    /// <param name="entries">Key to expression map (copied).</param>
    public ExpressionMapPropertyValue(IEnumerable<KeyValuePair<string, WorkflowExpression>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = new ReadOnlyDictionary<string, WorkflowExpression>(
            new Dictionary<string, WorkflowExpression>(entries, StringComparer.Ordinal));
    }

    /// <summary>Key to expression map.</summary>
    public IReadOnlyDictionary<string, WorkflowExpression> Entries { get; }
}

/// <summary>A map of keys to assignment target names.</summary>
public sealed class NameMapPropertyValue : PropertyValue
{
    /// <summary>Creates the value.</summary>
    /// <param name="entries">Key to target-name map (copied).</param>
    public NameMapPropertyValue(IEnumerable<KeyValuePair<string, string>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, name) in entries)
        {
            map.Add(key, WorkflowNames.EnsureValid(name, nameof(entries)));
        }

        Entries = new ReadOnlyDictionary<string, string>(map);
    }

    /// <summary>Key to target-name map.</summary>
    public IReadOnlyDictionary<string, string> Entries { get; }
}
