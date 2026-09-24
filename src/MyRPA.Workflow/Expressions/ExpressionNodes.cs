namespace MyRPA.Workflow.Expressions;

/// <summary>Parsed expression syntax tree (internal; exposed through <see cref="WorkflowExpression"/>).</summary>
internal abstract record ExpressionNode(int Position);

internal sealed record LiteralNode(object? Value, int Position) : ExpressionNode(Position);

internal sealed record NameNode(string Name, int Position) : ExpressionNode(Position);

internal sealed record UnaryNode(string Operator, ExpressionNode Operand, int Position) : ExpressionNode(Position);

internal sealed record BinaryNode(string Operator, ExpressionNode Left, ExpressionNode Right, int Position) : ExpressionNode(Position);

internal sealed record IndexNode(ExpressionNode Target, ExpressionNode Index, int Position) : ExpressionNode(Position);

internal sealed record MemberNode(ExpressionNode Target, string Member, int Position) : ExpressionNode(Position);

internal sealed record CallNode(string Function, IReadOnlyList<ExpressionNode> Arguments, int Position) : ExpressionNode(Position);

internal sealed record ListNode(IReadOnlyList<ExpressionNode> Items, int Position) : ExpressionNode(Position);

internal sealed record DictionaryNode(IReadOnlyList<KeyValuePair<string, ExpressionNode>> Entries, int Position) : ExpressionNode(Position);
