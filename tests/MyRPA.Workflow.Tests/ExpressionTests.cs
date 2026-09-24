using MyRPA.Workflow.Expressions;
using MyRPA.Workflow.Values;

namespace MyRPA.Workflow.Tests;

public sealed class ExpressionTests
{
    private static readonly DateTimeOffset _fixedNow = new(2026, 9, 23, 8, 0, 0, TimeSpan.Zero);

    private static object? Eval(string expression, params (string Name, object? Value)[] names) =>
        WorkflowExpression.Parse(expression).Evaluate(new Scope(names));

    [Theory]
    [InlineData("1 + 2 * 3", 7L)]
    [InlineData("(1 + 2) * 3", 9L)]
    [InlineData("7 / 2", 3L)]
    [InlineData("7 % 3", 1L)]
    [InlineData("-5 + 2", -3L)]
    [InlineData("10 - 2 - 3", 5L)]
    public void Arithmetic_IntegerSemantics(string expression, long expected) => Assert.Equal(expected, Eval(expression));

    [Fact]
    public void Arithmetic_MixedIntAndDecimal_PromotesToDecimal()
    {
        Assert.Equal(3.5m, Eval("7 / 2.0"));
        Assert.Equal(1.5m, Eval("1 + 0.5"));
    }

    [Theory]
    [InlineData("1 / 0", "Division by zero")]
    [InlineData("9223372036854775807 + 1", "overflow")]
    [InlineData("'a' - 1", "cannot be applied")]
    [InlineData("true && 1", "requires Boolean")]
    [InlineData("missing + 1", "Unknown name 'missing'")]
    [InlineData("[1, 2][5]", "out of range")]
    [InlineData("{'a': 1}.b", "does not exist")]
    [InlineData("'text'.length", "can only be used on a Dictionary")]
    [InlineData("1 < 'a'", "Cannot compare")]
    public void Evaluation_Errors_AreReportedAsExpressionExceptions(string expression, string fragment)
    {
        var ex = Assert.Throws<WorkflowExpressionException>(() => Eval(expression));
        Assert.Contains(fragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("'a' + 1", "a1")]
    [InlineData("1 + 'a'", "1a")]
    [InlineData("'x' + null", "x")]
    [InlineData("\"double\" + ' single'", "double single")]
    [InlineData("'it\\'s'", "it's")]
    public void StringConcatenation(string expression, string expected) => Assert.Equal(expected, Eval(expression));

    [Theory]
    [InlineData("1 == 1.0", true)]
    [InlineData("'a' != 'b'", true)]
    [InlineData("2 >= 2", true)]
    [InlineData("'abc' < 'abd'", true)]
    [InlineData("[1, [2]] == [1, [2]]", true)]
    [InlineData("{'k': 1} == {'k': 1}", true)]
    [InlineData("null == null", true)]
    [InlineData("!(1 > 2) && (true || false)", true)]
    public void ComparisonAndLogic(string expression, bool expected) => Assert.Equal(expected, Eval(expression));

    [Fact]
    public void Logic_ShortCircuits_SoRightSideIsNotEvaluated()
    {
        Assert.Equal(false, Eval("false && missing"));
        Assert.Equal(true, Eval("true || missing"));
    }

    [Fact]
    public void Names_IndexAndMemberAccess()
    {
        var order = WorkflowValues.Dictionary([new("items", WorkflowValues.List([10L, 20L])), new("customer", "Acme")]);

        Assert.Equal(20L, Eval("order.items[1]", ("order", order)));
        Assert.Equal("Acme", Eval("order['customer']", ("order", order)));
        Assert.Equal("c", Eval("'abc'[2]"));
    }

    [Fact]
    public void Literals_ListAndDictionary()
    {
        var list = Assert.IsAssignableFrom<IReadOnlyList<object?>>(Eval("[1, 'two', true, null]"));
        Assert.Equal([1L, "two", true, null], list);

        var map = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Eval("{'a': 1, b: 2.5}"));
        Assert.Equal(1L, map["a"]);
        Assert.Equal(2.5m, map["b"]);
    }

    [Theory]
    [InlineData("len('abc')", 3L)]
    [InlineData("len([1, 2])", 2L)]
    [InlineData("upper('ab')", "AB")]
    [InlineData("lower('AB')", "ab")]
    [InlineData("trim('  x ')", "x")]
    [InlineData("contains('hello', 'ell')", true)]
    [InlineData("contains([1, 2], 2)", true)]
    [InlineData("startsWith('invoice-1', 'invoice')", true)]
    [InlineData("substring('abcdef', 2, 3)", "cde")]
    [InlineData("replace('a-b-c', '-', '+')", "a+b+c")]
    [InlineData("toString(12)", "12")]
    [InlineData("toInt('42')", 42L)]
    [InlineData("toInt(3.0)", 3L)]
    [InlineData("toBoolean('true')", true)]
    [InlineData("hasKey({'a': 1}, 'a')", true)]
    [InlineData("isNull(null)", true)]
    [InlineData("coalesce(null, null, 'x')", "x")]
    [InlineData("abs(-4)", 4L)]
    [InlineData("min(3, 2.5)", 2.5)]
    [InlineData("max(3, 2)", 3L)]
    [InlineData("round(5)", 5L)]
    public void Functions(string expression, object expected)
    {
        var actual = Eval(expression);
        if (expected is double d)
        {
            Assert.Equal((decimal)d, actual);
        }
        else
        {
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Functions_RoundDecimal_AwayFromZero() => Assert.Equal(2.35m, Eval("round(2.345, 2)"));

    [Fact]
    public void Functions_AppendAndKeys_ReturnNewValues()
    {
        var original = WorkflowValues.List([1L]);
        var appended = Assert.IsAssignableFrom<IReadOnlyList<object?>>(Eval("append(items, 2)", ("items", original)));

        Assert.Equal([1L, 2L], appended);
        Assert.Single(original);
        Assert.Equal(["x", "y"], Assert.IsAssignableFrom<IReadOnlyList<object?>>(Eval("keys({'x': 1, 'y': 2})")));
    }

    [Fact]
    public void Functions_Now_UsesInjectedClock() => Assert.Equal(_fixedNow, Eval("now()"));

    [Fact]
    public void Functions_ToDateTime_ParsesIso8601AsUtcWhenNoOffset() =>
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), Eval("toDateTime('2026-01-02T03:04:05')"));

    [Theory]
    [InlineData("1 +", "Unexpected")]
    [InlineData("a = 1", "Use '=='")]
    [InlineData("'unterminated", "Unterminated")]
    [InlineData("(1 + 2", "Expected ')'")]
    [InlineData("", "empty")]
    [InlineData("{'a': 1, 'a': 2}", "Duplicate dictionary key")]
    [InlineData("99999999999999999999", "out of range")]
    public void Parse_SyntaxErrors_HavePositions(string expression, string fragment)
    {
        Assert.False(WorkflowExpression.TryParse(expression, out _, out var error));
        Assert.Contains(fragment, error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.Position);
    }

    [Fact]
    public void Parse_EnforcesLengthAndDepthLimits()
    {
        Assert.False(WorkflowExpression.TryParse(new string('1', 5000), out _, out _));
        var deep = new string('(', 100) + "1" + new string(')', 100);
        Assert.False(WorkflowExpression.TryParse(deep, out _, out var error));
        Assert.Contains("nested", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ReportsReferencedNamesAndFunctions()
    {
        var expression = WorkflowExpression.Parse("upper(first) + ' ' + person.last + items[index]");

        Assert.Equal(["first", "index", "items", "person"], expression.ReferencedNames);
        Assert.Equal(["upper"], expression.ReferencedFunctions);
    }

    [Fact]
    public void Literal_FromJsonValues_IsConstant()
    {
        var literal = WorkflowExpression.Literal(5L, "5");
        Assert.True(literal.IsConstant);
        Assert.True(literal.IsJsonLiteral);
        Assert.Equal(5L, literal.Evaluate(new Scope([])));
        Assert.Throws<ArgumentException>(() => WorkflowExpression.Literal("text", "\"text\""));
    }

    [Fact]
    public void Evaluation_HasNoAccessToClrMembers()
    {
        // There is no reflection: member access only reads dictionary keys, and unknown functions are rejected.
        Assert.Throws<WorkflowExpressionException>(() => Eval("x.GetType", ("x", "text")));
        Assert.Throws<WorkflowExpressionException>(() => Eval("GetType()"));
    }

    private sealed class Scope((string Name, object? Value)[] values) : IExpressionScope
    {
        public TimeProvider TimeProvider { get; } = new FixedTime();

        public bool TryGetValue(string name, out object? value)
        {
            foreach (var (n, v) in values)
            {
                if (n == name)
                {
                    value = v;
                    return true;
                }
            }

            value = null;
            return false;
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => _fixedNow;
    }
}
