using System.Collections.Immutable;
using System.Globalization;

namespace MyRPA.Studio.Documents;

/// <summary>One step from a node to one of its nested nodes: a child index or a slot name.</summary>
public abstract record PathStep;

/// <summary>The child at <paramref name="Index"/>.</summary>
/// <param name="Index">Zero-based child index.</param>
public sealed record ChildStep(int Index) : PathStep
{
    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"children[{Index}]");
}

/// <summary>The node in slot <paramref name="Name"/>.</summary>
/// <param name="Name">Slot name.</param>
public sealed record SlotStep(string Name) : PathStep
{
    /// <inheritdoc />
    public override string ToString() => $"slots[{Name}]";
}

/// <summary>
/// The address of a node relative to the workflow root. Paths (not node ids) address nodes, so edits work even while a
/// draft has duplicate or empty ids. Value equality.
/// </summary>
public sealed class NodePath : IEquatable<NodePath>
{
    private NodePath(ImmutableList<PathStep> steps) => Steps = steps;

    /// <summary>The root node.</summary>
    public static NodePath Root { get; } = new([]);

    /// <summary>Steps from the root.</summary>
    public ImmutableList<PathStep> Steps { get; }

    /// <summary>Whether this is the root.</summary>
    public bool IsRoot => Steps.Count == 0;

    /// <summary>The parent path (root has none).</summary>
    public NodePath? Parent => IsRoot ? null : new NodePath(Steps.RemoveAt(Steps.Count - 1));

    /// <summary>The last step (null for the root).</summary>
    public PathStep? Last => IsRoot ? null : Steps[^1];

    /// <summary>The path of a child.</summary>
    /// <param name="index">Child index.</param>
    public NodePath Child(int index) => new(Steps.Add(new ChildStep(index)));

    /// <summary>The path of a slot's node.</summary>
    /// <param name="name">Slot name.</param>
    public NodePath Slot(string name) => new(Steps.Add(new SlotStep(name)));

    /// <summary>Whether <paramref name="other"/> is this path or below it.</summary>
    /// <param name="other">Another path.</param>
    public bool IsAncestorOrSelfOf(NodePath other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return other.Steps.Count >= Steps.Count && Steps.SequenceEqual(other.Steps.Take(Steps.Count));
    }

    /// <inheritdoc />
    public bool Equals(NodePath? other) => other is not null && Steps.SequenceEqual(other.Steps);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as NodePath);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var step in Steps)
        {
            hash.Add(step);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => IsRoot ? "/" : "/" + string.Join("/", Steps);

    /// <summary>Value equality.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator ==(NodePath? left, NodePath? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Value inequality.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    public static bool operator !=(NodePath? left, NodePath? right) => !(left == right);
}

/// <summary>Navigation and structural replacement in a draft tree.</summary>
public static class DraftTree
{
    /// <summary>Returns the node at <paramref name="path"/>, or null.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="path">The path.</param>
    public static NodeDraft? Find(WorkflowDraft draft, NodePath path)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(path);
        NodeDraft? node = draft.Root;
        foreach (var step in path.Steps)
        {
            node = step switch
            {
                ChildStep c when node is not null && c.Index >= 0 && c.Index < node.Children.Count => node.Children[c.Index],
                SlotStep s when node is not null => node.Slot(s.Name),
                _ => null,
            };

            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    /// <summary>Returns the node at <paramref name="path"/>, or throws.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="path">The path.</param>
    public static NodeDraft Get(WorkflowDraft draft, NodePath path) =>
        Find(draft, path) ?? throw new EditException($"There is no node at {path}.");

    /// <summary>Returns a draft in which the node at <paramref name="path"/> is replaced by <paramref name="replacement"/>.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="path">The path of an existing node.</param>
    /// <param name="replacement">The new node.</param>
    public static WorkflowDraft Replace(WorkflowDraft draft, NodePath path, NodeDraft replacement)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(replacement);
        return draft with { Root = Replace(draft.Root, path.Steps, 0, replacement) };
    }

    /// <summary>All nodes with their paths, depth-first (children before slots).</summary>
    /// <param name="draft">The draft.</param>
    public static IEnumerable<(NodePath Path, NodeDraft Node)> Walk(WorkflowDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return Walk(draft.Root, NodePath.Root);
    }

    private static IEnumerable<(NodePath Path, NodeDraft Node)> Walk(NodeDraft node, NodePath path)
    {
        yield return (path, node);
        for (var i = 0; i < node.Children.Count; i++)
        {
            foreach (var item in Walk(node.Children[i], path.Child(i)))
            {
                yield return item;
            }
        }

        foreach (var slot in node.Slots)
        {
            foreach (var item in Walk(slot.Node, path.Slot(slot.Name)))
            {
                yield return item;
            }
        }
    }

    private static NodeDraft Replace(NodeDraft node, ImmutableList<PathStep> steps, int depth, NodeDraft replacement)
    {
        if (depth == steps.Count)
        {
            return replacement;
        }

        switch (steps[depth])
        {
            case ChildStep c when c.Index >= 0 && c.Index < node.Children.Count:
                return node with { Children = node.Children.SetItem(c.Index, Replace(node.Children[c.Index], steps, depth + 1, replacement)) };
            case SlotStep s when node.Slots.FindIndex(e => e.Name == s.Name) is var index and >= 0:
                var slot = node.Slots[index];
                return node with { Slots = node.Slots.SetItem(index, slot with { Node = Replace(slot.Node, steps, depth + 1, replacement) }) };
            default:
                throw new EditException($"There is no node at step {steps[depth]}.");
        }
    }
}

/// <summary>An edit that cannot be applied to the document (for example dropping into a slot that is taken).</summary>
public sealed class EditException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public EditException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why the edit was refused.</param>
    public EditException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Why the edit was refused.</param>
    /// <param name="innerException">Cause.</param>
    public EditException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
