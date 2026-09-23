using MyRPA.Core.Activities;
using MyRPA.Core.Identifiers;

namespace MyRPA.Workflow;

/// <summary>
/// One usage of an activity inside a workflow definition. Nodes form a tree through <see cref="Children"/>.
/// Immutable.
/// </summary>
/// <remarks>
/// Phase 1 skeleton: identity, activity type and structure only. Node properties (inputs/outputs) are added in
/// Phase 2 together with the versioned JSON serializer and validator.
/// </remarks>
public sealed class NodeDefinition
{
    /// <summary>Creates a node definition.</summary>
    /// <param name="id">Node identifier, unique within the workflow.</param>
    /// <param name="type">Registered activity type name.</param>
    /// <param name="displayName">Optional human-readable label.</param>
    /// <param name="children">Child nodes, in order. <see langword="null"/> means none.</param>
    public NodeDefinition(
        NodeId id,
        ActivityTypeName type,
        string? displayName = null,
        IEnumerable<NodeDefinition>? children = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(type);

        var childArray = children?.ToArray() ?? [];
        if (Array.IndexOf(childArray, null) >= 0)
        {
            throw new ArgumentException("Children must not contain null.", nameof(children));
        }

        Id = id;
        Type = type;
        DisplayName = displayName;
        Children = Array.AsReadOnly(childArray);
    }

    /// <summary>Node identifier, unique within the workflow.</summary>
    public NodeId Id { get; }

    /// <summary>Registered activity type name.</summary>
    public ActivityTypeName Type { get; }

    /// <summary>Optional human-readable label.</summary>
    public string? DisplayName { get; }

    /// <summary>Child nodes, in order.</summary>
    public IReadOnlyList<NodeDefinition> Children { get; }

    /// <summary>Enumerates this node and all descendants, depth-first, pre-order.</summary>
    public IEnumerable<NodeDefinition> DescendantsAndSelf()
    {
        var stack = new Stack<NodeDefinition>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }
    }
}
