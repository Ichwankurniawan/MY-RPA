using MyRPA.Workflow.Values;

namespace MyRPA.Workflow.Expressions;

/// <summary>Evaluates parsed expressions over canonical workflow values. No reflection, no CLR member access.</summary>
internal static class ExpressionEvaluator
{
    public static object? Evaluate(ExpressionNode node, IExpressionScope scope) => node switch
    {
        LiteralNode literal => literal.Value,
        NameNode name => scope.TryGetValue(name.Name, out var value)
            ? value
            : throw new WorkflowExpressionException($"Unknown name '{name.Name}'."),
        UnaryNode unary => EvaluateUnary(unary, scope),
        BinaryNode binary => EvaluateBinary(binary, scope),
        IndexNode index => EvaluateIndex(Evaluate(index.Target, scope), Evaluate(index.Index, scope)),
        MemberNode member => EvaluateMember(Evaluate(member.Target, scope), member.Member),
        CallNode call => ExpressionFunctions.Invoke(call.Function, [.. call.Arguments.Select(a => Evaluate(a, scope))], scope),
        ListNode list => WorkflowValues.List(list.Items.Select(i => Evaluate(i, scope))),
        DictionaryNode dictionary => WorkflowValues.Dictionary(
            dictionary.Entries.Select(e => new KeyValuePair<string, object?>(e.Key, Evaluate(e.Value, scope)))),
        _ => throw new WorkflowExpressionException($"Unsupported expression node '{node.GetType().Name}'."),
    };

    private static string Describe(object? value) => $"a {WorkflowValues.TypeNameOf(value)}";

    private static object? EvaluateUnary(UnaryNode unary, IExpressionScope scope)
    {
        var operand = Evaluate(unary.Operand, scope);
        return (unary.Operator, operand) switch
        {
            ("!", bool b) => !b,
            ("-", long l) => Checked(() => checked(-l)),
            ("-", decimal d) => -d,
            _ => throw new WorkflowExpressionException($"Operator '{unary.Operator}' cannot be applied to {Describe(operand)}."),
        };
    }

    private static object? EvaluateBinary(BinaryNode binary, IExpressionScope scope)
    {
        switch (binary.Operator)
        {
            case "&&":
            case "||":
                var left = RequireBoolean(binary.Operator, Evaluate(binary.Left, scope));
                if (binary.Operator == "&&" ? !left : left)
                {
                    return left;
                }

                return RequireBoolean(binary.Operator, Evaluate(binary.Right, scope));
        }

        var l = Evaluate(binary.Left, scope);
        var r = Evaluate(binary.Right, scope);
        switch (binary.Operator)
        {
            case "==":
                return WorkflowValues.AreEqual(l, r);
            case "!=":
                return !WorkflowValues.AreEqual(l, r);
            case "<" or "<=" or ">" or ">=":
                if (!WorkflowValues.TryCompare(l, r, out var c))
                {
                    throw new WorkflowExpressionException($"Cannot compare {Describe(l)} with {Describe(r)}.");
                }

                return binary.Operator switch
                {
                    "<" => c < 0,
                    "<=" => c <= 0,
                    ">" => c > 0,
                    _ => c >= 0,
                };
            case "+" when l is string || r is string:
                return WorkflowValues.ToDisplayString(l) + WorkflowValues.ToDisplayString(r);
            case "+" when l is IReadOnlyList<object?> a && r is IReadOnlyList<object?> b
                && l is not IReadOnlyDictionary<string, object?> && r is not IReadOnlyDictionary<string, object?>:
                return WorkflowValues.List(a.Concat(b));
            default:
                return Arithmetic(binary.Operator, l, r);
        }
    }

    private static object Arithmetic(string op, object? l, object? r)
    {
        switch (l, r)
        {
            case (long a, long b):
                return op switch
                {
                    "+" => Checked(() => checked(a + b)),
                    "-" => Checked(() => checked(a - b)),
                    "*" => Checked(() => checked(a * b)),
                    "/" => b == 0 ? throw DivideByZero() : Checked(() => checked(a / b)),
                    "%" => b == 0 ? throw DivideByZero() : a % b,
                    _ => throw new WorkflowExpressionException($"Unknown operator '{op}'."),
                };
            case (long or decimal, long or decimal):
                var x = l is long la ? la : (decimal)l;
                var y = r is long lb ? lb : (decimal)r;
                return op switch
                {
                    "+" => Checked(() => x + y),
                    "-" => Checked(() => x - y),
                    "*" => Checked(() => x * y),
                    "/" => y == 0 ? throw DivideByZero() : Checked(() => x / y),
                    "%" => y == 0 ? throw DivideByZero() : x % y,
                    _ => throw new WorkflowExpressionException($"Unknown operator '{op}'."),
                };
            default:
                throw new WorkflowExpressionException($"Operator '{op}' cannot be applied to {Describe(l)} and {Describe(r)}.");
        }
    }

    private static WorkflowExpressionException DivideByZero() => new("Division by zero.");

    private static T Checked<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (OverflowException)
        {
            throw new WorkflowExpressionException("Arithmetic overflow.");
        }
    }

    private static bool RequireBoolean(string op, object? value) =>
        value is bool b ? b : throw new WorkflowExpressionException($"Operator '{op}' requires Boolean operands but got {Describe(value)}.");

    private static object? EvaluateIndex(object? target, object? index)
    {
        switch (target, index)
        {
            case (IReadOnlyDictionary<string, object?> d, string key):
                return d.TryGetValue(key, out var value) ? value : throw new WorkflowExpressionException($"Key '{key}' does not exist.");
            case (IReadOnlyList<object?> list, long i):
                return i >= 0 && i < list.Count ? list[(int)i] : throw OutOfRange(i, list.Count);
            case (string s, long i):
                return i >= 0 && i < s.Length ? s[(int)i].ToString() : throw OutOfRange(i, s.Length);
            default:
                throw new WorkflowExpressionException($"Cannot index {Describe(target)} with {Describe(index)}.");
        }
    }

    private static WorkflowExpressionException OutOfRange(long index, int count) =>
        new(FormattableString.Invariant($"Index {index} is out of range (length {count})."));

    private static object? EvaluateMember(object? target, string member) =>
        target is IReadOnlyDictionary<string, object?> d
            ? d.TryGetValue(member, out var value) ? value : throw new WorkflowExpressionException($"Key '{member}' does not exist.")
            : throw new WorkflowExpressionException($"'.{member}' can only be used on a Dictionary, not on {Describe(target)}.");
}
