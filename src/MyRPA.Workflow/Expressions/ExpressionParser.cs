using System.Globalization;
using System.Text;

namespace MyRPA.Workflow.Expressions;

/// <summary>Recursive-descent parser for the workflow expression language (ADR-0009).</summary>
internal sealed class ExpressionParser
{
    public const int MaxLength = 4096;
    public const int MaxDepth = 64;

    private readonly string _text;
    private readonly List<Token> _tokens;
    private int _index;
    private int _depth;

    private ExpressionParser(string text, List<Token> tokens)
    {
        _text = text;
        _tokens = tokens;
    }

    private enum TokenKind
    {
        Number,
        String,
        Identifier,
        Symbol,
        End,
    }

    public static ExpressionNode Parse(string text)
    {
        if (text.Length > MaxLength)
        {
            throw new WorkflowExpressionException($"Expression is longer than {MaxLength} characters.", Truncate(text), 0);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new WorkflowExpressionException("Expression is empty.", text, 0);
        }

        var parser = new ExpressionParser(text, Tokenize(text));
        var node = parser.ParseOr();
        var next = parser.Peek();
        if (next.Kind != TokenKind.End)
        {
            throw parser.Error($"Unexpected '{next.Text}'", next.Position);
        }

        return node;
    }

    private static string Truncate(string text) => text.Length <= 64 ? text : text[..64] + "...";

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;
            if (char.IsAsciiDigit(c))
            {
                while (i < text.Length && char.IsAsciiDigit(text[i]))
                {
                    i++;
                }

                var isDecimal = false;
                if (i + 1 < text.Length && text[i] == '.' && char.IsAsciiDigit(text[i + 1]))
                {
                    isDecimal = true;
                    i++;
                    while (i < text.Length && char.IsAsciiDigit(text[i]))
                    {
                        i++;
                    }
                }

                var literal = text[start..i];
                object value;
                if (isDecimal)
                {
                    if (!decimal.TryParse(literal, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d))
                    {
                        throw new WorkflowExpressionException($"Number '{literal}' is out of range.", text, start);
                    }

                    value = d;
                }
                else
                {
                    if (!long.TryParse(literal, NumberStyles.None, CultureInfo.InvariantCulture, out var l))
                    {
                        throw new WorkflowExpressionException($"Number '{literal}' is out of range.", text, start);
                    }

                    value = l;
                }

                tokens.Add(new Token(TokenKind.Number, literal, value, start));
                continue;
            }

            if (c is '\'' or '"')
            {
                var builder = new StringBuilder();
                i++;
                var closed = false;
                while (i < text.Length)
                {
                    var ch = text[i];
                    if (ch == c)
                    {
                        closed = true;
                        i++;
                        break;
                    }

                    if (ch == '\\' && i + 1 < text.Length)
                    {
                        var escaped = text[i + 1];
                        builder.Append(escaped switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            '\\' or '\'' or '"' => escaped,
                            _ => throw new WorkflowExpressionException($"Unknown escape '\\{escaped}'.", text, i),
                        });
                        i += 2;
                        continue;
                    }

                    builder.Append(ch);
                    i++;
                }

                if (!closed)
                {
                    throw new WorkflowExpressionException("Unterminated string literal.", text, start);
                }

                tokens.Add(new Token(TokenKind.String, text[start..i], builder.ToString(), start));
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Identifier, text[start..i], null, start));
                continue;
            }

            var two = i + 1 < text.Length ? text.Substring(i, 2) : null;
            if (two is "==" or "!=" or "<=" or ">=" or "&&" or "||")
            {
                tokens.Add(new Token(TokenKind.Symbol, two, null, start));
                i += 2;
                continue;
            }

            if ("+-*/%!<>()[]{},.:".Contains(c, StringComparison.Ordinal))
            {
                tokens.Add(new Token(TokenKind.Symbol, c.ToString(), null, start));
                i++;
                continue;
            }

            var hint = c == '=' ? " Use '==' to compare; assignment is done by Core.Assign." : string.Empty;
            throw new WorkflowExpressionException($"Unexpected character '{c}'.{hint}", text, start);
        }

        tokens.Add(new Token(TokenKind.End, "end of expression", null, text.Length));
        return tokens;
    }

    private Token Peek() => _tokens[_index];

    private Token Next() => _tokens[_index++];

    private bool TryConsume(string symbol)
    {
        var token = Peek();
        if (token.Kind == TokenKind.Symbol && token.Text == symbol)
        {
            _index++;
            return true;
        }

        return false;
    }

    private void Expect(string symbol)
    {
        var token = Peek();
        if (!TryConsume(symbol))
        {
            throw Error($"Expected '{symbol}' but found '{token.Text}'", token.Position);
        }
    }

    private WorkflowExpressionException Error(string message, int position) => new(message, _text, position);

    private ExpressionNode Enter(Func<ExpressionNode> parse, int position)
    {
        if (++_depth > MaxDepth)
        {
            throw Error($"Expression is nested more than {MaxDepth} levels deep", position);
        }

        try
        {
            return parse();
        }
        finally
        {
            _depth--;
        }
    }

    private ExpressionNode ParseOr() => ParseBinary(ParseAnd, "||");

    private ExpressionNode ParseAnd() => ParseBinary(ParseEquality, "&&");

    private ExpressionNode ParseEquality() => ParseBinary(ParseComparison, "==", "!=");

    private ExpressionNode ParseComparison() => ParseBinary(ParseAdditive, "<", "<=", ">", ">=");

    private ExpressionNode ParseAdditive() => ParseBinary(ParseMultiplicative, "+", "-");

    private ExpressionNode ParseMultiplicative() => ParseBinary(ParseUnary, "*", "/", "%");

    private ExpressionNode ParseBinary(Func<ExpressionNode> operand, params string[] operators)
    {
        var left = operand();
        while (true)
        {
            var token = Peek();
            if (token.Kind != TokenKind.Symbol || Array.IndexOf(operators, token.Text) < 0)
            {
                return left;
            }

            _index++;
            var right = operand();
            left = new BinaryNode(token.Text, left, right, token.Position);
        }
    }

    private ExpressionNode ParseUnary()
    {
        var token = Peek();
        if (token.Kind == TokenKind.Symbol && token.Text is "!" or "-")
        {
            _index++;
            return Enter(() => new UnaryNode(token.Text, ParseUnary(), token.Position), token.Position);
        }

        return ParsePostfix();
    }

    private ExpressionNode ParsePostfix()
    {
        var node = ParsePrimary();
        while (true)
        {
            var token = Peek();
            if (TryConsume("["))
            {
                var index = Enter(ParseOr, token.Position);
                Expect("]");
                node = new IndexNode(node, index, token.Position);
            }
            else if (TryConsume("."))
            {
                var member = Next();
                if (member.Kind != TokenKind.Identifier)
                {
                    throw Error($"Expected a key name after '.' but found '{member.Text}'", member.Position);
                }

                node = new MemberNode(node, member.Text, token.Position);
            }
            else
            {
                return node;
            }
        }
    }

    private ExpressionNode ParsePrimary()
    {
        var token = Next();
        switch (token.Kind)
        {
            case TokenKind.Number:
            case TokenKind.String:
                return new LiteralNode(token.Value, token.Position);
            case TokenKind.Identifier:
                switch (token.Text)
                {
                    case "true":
                        return new LiteralNode(true, token.Position);
                    case "false":
                        return new LiteralNode(false, token.Position);
                    case "null":
                        return new LiteralNode(null, token.Position);
                }

                if (TryConsume("("))
                {
                    var arguments = ParseList(")", token.Position);
                    return new CallNode(token.Text, arguments, token.Position);
                }

                return new NameNode(token.Text, token.Position);
            case TokenKind.Symbol when token.Text == "(":
                var inner = Enter(ParseOr, token.Position);
                Expect(")");
                return inner;
            case TokenKind.Symbol when token.Text == "[":
                return new ListNode(ParseList("]", token.Position), token.Position);
            case TokenKind.Symbol when token.Text == "{":
                return Enter(() => ParseDictionary(token.Position), token.Position);
            default:
                throw Error($"Unexpected '{token.Text}'", token.Position);
        }
    }

    private List<ExpressionNode> ParseList(string close, int position)
    {
        var items = new List<ExpressionNode>();
        if (TryConsume(close))
        {
            return items;
        }

        do
        {
            items.Add(Enter(ParseOr, position));
        }
        while (TryConsume(","));

        Expect(close);
        return items;
    }

    private DictionaryNode ParseDictionary(int position)
    {
        var entries = new List<KeyValuePair<string, ExpressionNode>>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (!TryConsume("}"))
        {
            do
            {
                var key = Next();
                if (key.Kind is not (TokenKind.String or TokenKind.Identifier))
                {
                    throw Error($"Expected a dictionary key but found '{key.Text}'", key.Position);
                }

                var keyText = key.Kind == TokenKind.String ? (string)key.Value! : key.Text;
                if (!keys.Add(keyText))
                {
                    throw Error($"Duplicate dictionary key '{keyText}'", key.Position);
                }

                Expect(":");
                entries.Add(new(keyText, ParseOr()));
            }
            while (TryConsume(","));

            Expect("}");
        }

        return new DictionaryNode(entries, position);
    }

    private readonly record struct Token(TokenKind Kind, string Text, object? Value, int Position);
}
