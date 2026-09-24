using System.Diagnostics.CodeAnalysis;

namespace MyRPA.Workflow.Expressions;

/// <summary>
/// A parsed, immutable workflow expression (ADR-0009). Parsed once during loading; evaluated many times.
/// </summary>
public sealed class WorkflowExpression
{
    private readonly ExpressionNode _root;

    private WorkflowExpression(string source, ExpressionNode root, bool isJsonLiteral)
    {
        Source = source;
        _root = root;
        IsJsonLiteral = isJsonLiteral;

        var names = new SortedSet<string>(StringComparer.Ordinal);
        var functions = new SortedSet<string>(StringComparer.Ordinal);
        var calls = new List<(string Name, int Arguments)>();
        Collect(root, names, functions, calls);
        ReferencedNames = [.. names];
        ReferencedFunctions = [.. functions];
        FunctionCalls = calls;
    }

    /// <summary>The expression text (for JSON literals, the raw JSON text).</summary>
    public string Source { get; }

    /// <summary>Whether the expression came from a JSON number/boolean/null rather than expression text.</summary>
    public bool IsJsonLiteral { get; }

    /// <summary>Variable/argument/local names referenced by the expression.</summary>
    public IReadOnlyList<string> ReferencedNames { get; }

    /// <summary>Function names called by the expression.</summary>
    public IReadOnlyList<string> ReferencedFunctions { get; }

    /// <summary>Whether the expression is a constant literal.</summary>
    public bool IsConstant => _root is LiteralNode;

    internal IReadOnlyList<(string Name, int Arguments)> FunctionCalls { get; }

    /// <summary>Parses expression text.</summary>
    /// <param name="source">Expression text.</param>
    /// <exception cref="WorkflowExpressionException">The text is not a valid expression.</exception>
    public static WorkflowExpression Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new WorkflowExpression(source, ExpressionParser.Parse(source), isJsonLiteral: false);
    }

    /// <summary>Parses expression text without throwing.</summary>
    /// <param name="source">Expression text.</param>
    /// <param name="expression">The parsed expression.</param>
    /// <param name="error">The syntax error.</param>
    public static bool TryParse(string source, [NotNullWhen(true)] out WorkflowExpression? expression, [NotNullWhen(false)] out WorkflowExpressionException? error)
    {
        try
        {
            expression = Parse(source);
            error = null;
            return true;
        }
        catch (WorkflowExpressionException ex)
        {
            expression = null;
            error = ex;
            return false;
        }
    }

    /// <summary>Creates a constant expression from a canonical literal (a JSON number, boolean or null).</summary>
    /// <param name="value">Canonical <see cref="long"/>, <see cref="decimal"/>, <see cref="bool"/> or <see langword="null"/>.</param>
    /// <param name="source">Raw JSON text of the literal.</param>
    public static WorkflowExpression Literal(object? value, string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (value is not (null or long or decimal or bool))
        {
            throw new ArgumentException("Only Int, Decimal, Boolean and null literals are supported.", nameof(value));
        }

        return new WorkflowExpression(source, new LiteralNode(value, 0), isJsonLiteral: true);
    }

    /// <summary>Evaluates the expression.</summary>
    /// <param name="scope">Name values and clock.</param>
    /// <returns>A canonical workflow value.</returns>
    /// <exception cref="WorkflowExpressionException">Evaluation failed (unknown name, type error, overflow, ...).</exception>
    public object? Evaluate(IExpressionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        try
        {
            return ExpressionEvaluator.Evaluate(_root, scope);
        }
        catch (WorkflowExpressionException ex) when (ex.Expression is null)
        {
            throw new WorkflowExpressionException($"{ex.Message} Expression: {Source}", ex);
        }
    }

    /// <inheritdoc />
    public override string ToString() => Source;

    private static void Collect(ExpressionNode node, SortedSet<string> names, SortedSet<string> functions, List<(string, int)> calls)
    {
        switch (node)
        {
            case NameNode n:
                names.Add(n.Name);
                break;
            case UnaryNode u:
                Collect(u.Operand, names, functions, calls);
                break;
            case BinaryNode b:
                Collect(b.Left, names, functions, calls);
                Collect(b.Right, names, functions, calls);
                break;
            case IndexNode i:
                Collect(i.Target, names, functions, calls);
                Collect(i.Index, names, functions, calls);
                break;
            case MemberNode m:
                Collect(m.Target, names, functions, calls);
                break;
            case CallNode c:
                functions.Add(c.Function);
                calls.Add((c.Function, c.Arguments.Count));
                foreach (var argument in c.Arguments)
                {
                    Collect(argument, names, functions, calls);
                }

                break;
            case ListNode l:
                foreach (var item in l.Items)
                {
                    Collect(item, names, functions, calls);
                }

                break;
            case DictionaryNode d:
                foreach (var entry in d.Entries)
                {
                    Collect(entry.Value, names, functions, calls);
                }

                break;
        }
    }

    /// <summary>Returns the first problem with the functions used (unknown function or wrong argument count).</summary>
    internal string? FindFunctionProblem()
    {
        foreach (var (name, count) in FunctionCalls)
        {
            if (!ExpressionFunctions.TryGetArity(name, out var min, out var max))
            {
                return $"Unknown function '{name}'.";
            }

            if (count < min || count > max)
            {
                return $"{name}() takes {ExpressionFunctions.DescribeArity(name)} argument(s) but is called with {count}.";
            }
        }

        return null;
    }
}
