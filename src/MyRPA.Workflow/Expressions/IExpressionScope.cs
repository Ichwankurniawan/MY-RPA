namespace MyRPA.Workflow.Expressions;

/// <summary>Supplies name values and the clock to expression evaluation.</summary>
public interface IExpressionScope
{
    /// <summary>The clock used by <c>now()</c>.</summary>
    TimeProvider TimeProvider { get; }

    /// <summary>Looks up a variable, argument or local by name.</summary>
    /// <param name="name">Name used in the expression.</param>
    /// <param name="value">Canonical value.</param>
    bool TryGetValue(string name, out object? value);
}
