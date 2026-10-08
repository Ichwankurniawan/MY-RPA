using System.Collections.ObjectModel;
using MyRPA.Core.Activities;
using MyRPA.Core.Identifiers;

namespace MyRPA.Workflow;

/// <summary>
/// One usage of an activity inside a workflow definition. Nodes form a tree through <see cref="Children"/>
/// (ordered list, e.g. a Sequence) and <see cref="Slots"/> (named single children, e.g. <c>then</c>/<c>else</c>).
/// A step of a graph container may also have <see cref="Transitions"/> to its siblings (format 1.1, ADR-0037).
/// Immutable. Constructors enforce structural invariants only; activity-specific rules are validated by
/// <see cref="Validation.WorkflowLoader"/>.
/// </summary>
public sealed class NodeDefinition
{
    /// <summary>Creates a node definition.</summary>
    /// <param name="id">Node identifier, unique within the workflow.</param>
    /// <param name="type">Registered activity type name.</param>
    /// <param name="displayName">Optional human-readable label.</param>
    /// <param name="children">Ordered child nodes. <see langword="null"/> means none.</param>
    /// <param name="properties">Property values by name. <see langword="null"/> means none.</param>
    /// <param name="slots">Named single children. <see langword="null"/> means none.</param>
    /// <param name="transitions">Ordered transitions to sibling steps (graph containers only). <see langword="null"/> means none.</param>
    /// <param name="layout">Optional designer position.</param>
    public NodeDefinition(
        NodeId id,
        ActivityTypeName type,
        string? displayName = null,
        IEnumerable<NodeDefinition>? children = null,
        IEnumerable<KeyValuePair<string, PropertyValue>>? properties = null,
        IEnumerable<KeyValuePair<string, NodeDefinition>>? slots = null,
        IEnumerable<TransitionDefinition>? transitions = null,
        NodeLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(type);

        var childArray = children?.ToArray() ?? [];
        if (Array.IndexOf(childArray, null) >= 0)
        {
            throw new ArgumentException("Children must not contain null.", nameof(children));
        }

        var propertyMap = new Dictionary<string, PropertyValue>(StringComparer.Ordinal);
        foreach (var (name, value) in properties ?? [])
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(properties));
            propertyMap.Add(name, value ?? throw new ArgumentException("Property values must not be null.", nameof(properties)));
        }

        var slotMap = new Dictionary<string, NodeDefinition>(StringComparer.Ordinal);
        foreach (var (name, node) in slots ?? [])
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(slots));
            slotMap.Add(name, node ?? throw new ArgumentException("Slot nodes must not be null.", nameof(slots)));
        }

        var transitionArray = transitions?.ToArray() ?? [];
        if (Array.IndexOf(transitionArray, null) >= 0)
        {
            throw new ArgumentException("Transitions must not contain null.", nameof(transitions));
        }

        Id = id;
        Type = type;
        DisplayName = displayName;
        Children = Array.AsReadOnly(childArray);
        Properties = new ReadOnlyDictionary<string, PropertyValue>(propertyMap);
        Slots = new ReadOnlyDictionary<string, NodeDefinition>(slotMap);
        Transitions = Array.AsReadOnly(transitionArray);
        Layout = layout;
    }

    /// <summary>Node identifier, unique within the workflow.</summary>
    public NodeId Id { get; }

    /// <summary>Registered activity type name.</summary>
    public ActivityTypeName Type { get; }

    /// <summary>Optional human-readable label.</summary>
    public string? DisplayName { get; }

    /// <summary>Ordered child nodes.</summary>
    public IReadOnlyList<NodeDefinition> Children { get; }

    /// <summary>Property values by name.</summary>
    public IReadOnlyDictionary<string, PropertyValue> Properties { get; }

    /// <summary>Named single children (slot name to node), in declaration order.</summary>
    public IReadOnlyDictionary<string, NodeDefinition> Slots { get; }

    /// <summary>Ordered transitions to sibling steps; empty unless the parent is a graph container (ADR-0037).</summary>
    public IReadOnlyList<TransitionDefinition> Transitions { get; }

    /// <summary>The designer position, if any. The engine ignores it.</summary>
    public NodeLayout? Layout { get; }

    /// <summary>Enumerates this node and all descendants (children, then slots), depth-first, pre-order.</summary>
    public IEnumerable<NodeDefinition> DescendantsAndSelf()
    {
        var stack = new Stack<NodeDefinition>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            var next = node.Children.Concat(node.Slots.Values).ToArray();
            for (var i = next.Length - 1; i >= 0; i--)
            {
                stack.Push(next[i]);
            }
        }
    }
}
