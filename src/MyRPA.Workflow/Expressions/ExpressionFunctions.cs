using System.Collections.Frozen;
using System.Globalization;
using MyRPA.Workflow.Values;

namespace MyRPA.Workflow.Expressions;

/// <summary>The fixed whitelist of functions available in expressions (ADR-0009). Pure, except <c>now()</c>.</summary>
public static class ExpressionFunctions
{
    private static readonly FrozenDictionary<string, FunctionDefinition> _functions = new FunctionDefinition[]
    {
        new("len", 1, 1, (a, _) => a[0] switch
        {
            string s => (long)s.Length,
            IReadOnlyDictionary<string, object?> d => (long)d.Count,
            IReadOnlyList<object?> l => (long)l.Count,
            var v => throw TypeError("len", v),
        }),
        new("upper", 1, 1, (a, _) => Str("upper", a[0]).ToUpperInvariant()),
#pragma warning disable CA1308 // lower() is a user-facing string function, not normalization.
        new("lower", 1, 1, (a, _) => Str("lower", a[0]).ToLowerInvariant()),
#pragma warning restore CA1308
        new("trim", 1, 1, (a, _) => Str("trim", a[0]).Trim()),
        new("contains", 2, 2, (a, _) => a[0] switch
        {
            string s => s.Contains(Str("contains", a[1]), StringComparison.Ordinal),
            IReadOnlyDictionary<string, object?> d => d.ContainsKey(Str("contains", a[1])),
            IReadOnlyList<object?> l => l.Any(item => WorkflowValues.AreEqual(item, a[1])),
            var v => throw TypeError("contains", v),
        }),
        new("startsWith", 2, 2, (a, _) => Str("startsWith", a[0]).StartsWith(Str("startsWith", a[1]), StringComparison.Ordinal)),
        new("endsWith", 2, 2, (a, _) => Str("endsWith", a[0]).EndsWith(Str("endsWith", a[1]), StringComparison.Ordinal)),
        new("substring", 2, 3, (a, _) => Substring(a)),
        new("replace", 3, 3, (a, _) =>
        {
            var oldValue = Str("replace", a[1]);
            if (oldValue.Length == 0)
            {
                throw new WorkflowExpressionException("replace(): the text to replace must not be empty.");
            }

            return Str("replace", a[0]).Replace(oldValue, Str("replace", a[2]), StringComparison.Ordinal);
        }),
        new("toString", 1, 1, (a, _) => WorkflowValues.ToDisplayString(a[0])),
        new("toInt", 1, 1, (a, _) => ToInt(a[0])),
        new("toDecimal", 1, 1, (a, _) => a[0] switch
        {
            long l => (decimal)l,
            decimal d => d,
            string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
            var v => throw new WorkflowExpressionException($"toDecimal(): cannot convert {Describe(v)}."),
        }),
        new("toBoolean", 1, 1, (a, _) => a[0] switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var b) => b,
            var v => throw new WorkflowExpressionException($"toBoolean(): cannot convert {Describe(v)}."),
        }),
        new("toDateTime", 1, 1, (a, _) => a[0] switch
        {
            DateTimeOffset d => d,
            string s when WorkflowValues.TryParseDateTime(s, out var d) => d,
            var v => throw new WorkflowExpressionException($"toDateTime(): cannot convert {Describe(v)}."),
        }),
        new("now", 0, 0, (_, scope) => scope.TimeProvider.GetUtcNow()),
        new("append", 2, 2, (a, _) => a[0] is IReadOnlyList<object?> l and not IReadOnlyDictionary<string, object?>
            ? WorkflowValues.List(l.Append(a[1]))
            : throw TypeError("append", a[0])),
        new("keys", 1, 1, (a, _) => a[0] is IReadOnlyDictionary<string, object?> d
            ? WorkflowValues.List(d.Keys.Cast<object?>())
            : throw TypeError("keys", a[0])),
        new("hasKey", 2, 2, (a, _) => a[0] is IReadOnlyDictionary<string, object?> d
            ? d.ContainsKey(Str("hasKey", a[1]))
            : throw TypeError("hasKey", a[0])),
        new("isNull", 1, 1, (a, _) => a[0] is null),
        new("coalesce", 1, 8, (a, _) => a.FirstOrDefault(v => v is not null)),
        new("abs", 1, 1, (a, _) => a[0] switch
        {
            long l => (object)checked(Math.Abs(l)),
            decimal d => Math.Abs(d),
            var v => throw TypeError("abs", v),
        }),
        new("min", 2, 2, (a, _) => MinMax("min", a, pickLeftWhen: c => c <= 0)),
        new("max", 2, 2, (a, _) => MinMax("max", a, pickLeftWhen: c => c >= 0)),
        new("round", 1, 2, (a, _) => Round(a)),
    }.ToFrozenDictionary(f => f.Name, StringComparer.Ordinal);

    /// <summary>Names of all available functions.</summary>
    public static IEnumerable<string> Names => _functions.Keys;

    /// <summary>Returns the accepted argument count range of a function.</summary>
    /// <param name="name">Function name.</param>
    /// <param name="minArguments">Minimum argument count.</param>
    /// <param name="maxArguments">Maximum argument count.</param>
    public static bool TryGetArity(string name, out int minArguments, out int maxArguments)
    {
        if (_functions.TryGetValue(name, out var function))
        {
            minArguments = function.MinArguments;
            maxArguments = function.MaxArguments;
            return true;
        }

        minArguments = maxArguments = 0;
        return false;
    }

    internal static object? Invoke(string name, IReadOnlyList<object?> arguments, IExpressionScope scope)
    {
        if (!_functions.TryGetValue(name, out var function))
        {
            throw new WorkflowExpressionException($"Unknown function '{name}'.");
        }

        if (arguments.Count < function.MinArguments || arguments.Count > function.MaxArguments)
        {
            throw new WorkflowExpressionException($"{name}() takes {DescribeArity(function)} argument(s) but got {arguments.Count}.");
        }

        return function.Implementation(arguments, scope);
    }

    internal static string DescribeArity(string name) =>
        _functions.TryGetValue(name, out var f) ? DescribeArity(f) : "?";

    private static string DescribeArity(FunctionDefinition f) =>
        f.MinArguments == f.MaxArguments
            ? f.MinArguments.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{f.MinArguments}-{f.MaxArguments}");

    private static string Describe(object? value) => $"a {WorkflowValues.TypeNameOf(value)} value";

    private static WorkflowExpressionException TypeError(string function, object? value) =>
        new($"{function}() does not accept {Describe(value)}.");

    private static string Str(string function, object? value) =>
        value as string ?? throw new WorkflowExpressionException($"{function}() expects a String but got {Describe(value)}.");

    private static long ToLong(string function, object? value) =>
        value as long? ?? throw new WorkflowExpressionException($"{function}() expects an Int but got {Describe(value)}.");

    private static string Substring(IReadOnlyList<object?> a)
    {
        var s = Str("substring", a[0]);
        var start = ToLong("substring", a[1]);
        var length = a.Count > 2 ? ToLong("substring", a[2]) : s.Length - start;
        if (start < 0 || length < 0 || start + length > s.Length)
        {
            throw new WorkflowExpressionException(
                string.Create(CultureInfo.InvariantCulture, $"substring(): range {start}+{length} is outside a string of length {s.Length}."));
        }

        return s.Substring((int)start, (int)length);
    }

    private static long ToInt(object? value)
    {
        switch (value)
        {
            case long l:
                return l;
            case decimal d when d == decimal.Truncate(d) && d >= long.MinValue && d <= long.MaxValue:
                return (long)d;
            case string s when long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l):
                return l;
            default:
                throw new WorkflowExpressionException($"toInt(): cannot convert {Describe(value)} to Int.");
        }
    }

    private static object? MinMax(string function, IReadOnlyList<object?> a, Func<int, bool> pickLeftWhen)
    {
        if (a[0] is not (long or decimal) || a[1] is not (long or decimal) || !WorkflowValues.TryCompare(a[0], a[1], out var c))
        {
            throw new WorkflowExpressionException($"{function}() expects two numbers.");
        }

        return pickLeftWhen(c) ? a[0] : a[1];
    }

    private static object Round(IReadOnlyList<object?> a)
    {
        var digits = a.Count > 1 ? ToLong("round", a[1]) : 0;
        if (digits is < 0 or > 28)
        {
            throw new WorkflowExpressionException("round(): digits must be between 0 and 28.");
        }

        // Explicit object arms: without them the switch's natural type is decimal and an Int would silently widen.
        return a[0] switch
        {
            long l => (object)l,
            decimal d => (object)Math.Round(d, (int)digits, MidpointRounding.AwayFromZero),
            var v => throw TypeError("round", v),
        };
    }

    private sealed record FunctionDefinition(
        string Name,
        int MinArguments,
        int MaxArguments,
        Func<IReadOnlyList<object?>, IExpressionScope, object?> Implementation);
}
