namespace MyRPA.Workflow.Expressions;

/// <summary>An expression could not be parsed or evaluated.</summary>
public sealed class WorkflowExpressionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public WorkflowExpressionException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Error message.</param>
    public WorkflowExpressionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Error message.</param>
    /// <param name="innerException">Cause.</param>
    public WorkflowExpressionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a specific expression.</summary>
    /// <param name="message">Error message.</param>
    /// <param name="expression">Expression source text.</param>
    /// <param name="position">Zero-based character position of the problem.</param>
    public WorkflowExpressionException(string message, string expression, int position)
        : base($"{message} (in expression \"{expression}\" at position {position})")
    {
        Expression = expression;
        Position = position;
    }

    /// <summary>The expression source text, when known.</summary>
    public string? Expression { get; }

    /// <summary>Zero-based character position of the problem, when known.</summary>
    public int? Position { get; }
}
