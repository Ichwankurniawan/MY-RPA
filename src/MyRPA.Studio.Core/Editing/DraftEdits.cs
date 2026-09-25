using System.Globalization;
using MyRPA.Core.Activities;
using MyRPA.Studio.Documents;

namespace MyRPA.Studio.Editing;

/// <summary>Where a node goes inside a parent: at a child index, or into a named slot.</summary>
public abstract record InsertPosition;

/// <summary>Insert as child number <paramref name="Index"/> (0 … child count).</summary>
/// <param name="Index">Target index.</param>
public sealed record ChildPosition(int Index) : InsertPosition;

/// <summary>Insert into slot <paramref name="Name"/> (which must be empty).</summary>
/// <param name="Name">Slot name.</param>
public sealed record SlotPosition(string Name) : InsertPosition;

/// <summary>
/// The document edits (ADR-0018). Each is a pure function from a draft to a new draft; structural rules come from the
/// activity descriptors (which parents accept children, which slots exist). Invalid edits throw
/// <see cref="EditException"/> and leave the document unchanged. Property values are not checked here — that is
/// validation's job, so users can type unfinished expressions.
/// </summary>
public sealed class DraftEdits(IActivityCatalog catalog)
{
    private readonly IActivityCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <summary>Returns whether a node may be placed at <paramref name="position"/> under <paramref name="parentPath"/>.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="parentPath">The parent.</param>
    /// <param name="position">The position.</param>
    /// <param name="reason">Why not, when false.</param>
    public bool CanInsert(WorkflowDraft draft, NodePath parentPath, InsertPosition position, out string reason)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(position);
        var parent = DraftTree.Find(draft, parentPath);
        if (parent is null)
        {
            reason = $"There is no node at {parentPath}.";
            return false;
        }

        if (!ActivityTypeName.TryCreate(parent.Type, out var type) || !_catalog.TryGet(type, out var descriptor))
        {
            reason = $"'{parent.Type}' is not a registered activity; its children and slots are unknown.";
            return false;
        }

        switch (position)
        {
            case ChildPosition child:
                if (!descriptor.AllowsChildren)
                {
                    reason = $"{descriptor.DisplayName} does not contain a list of activities.";
                    return false;
                }

                if (child.Index < 0 || child.Index > parent.Children.Count)
                {
                    reason = "The position is outside the list.";
                    return false;
                }

                break;
            case SlotPosition slot:
                if (descriptor.FindSlot(slot.Name) is null)
                {
                    reason = $"{descriptor.DisplayName} has no slot '{slot.Name}'.";
                    return false;
                }

                if (parent.Slot(slot.Name) is not null)
                {
                    reason = $"The slot '{slot.Name}' already contains an activity.";
                    return false;
                }

                break;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Inserts a node.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="parentPath">The parent.</param>
    /// <param name="position">Where.</param>
    /// <param name="node">The node (with its subtree).</param>
    /// <returns>The new draft and the path of the inserted node.</returns>
    public (WorkflowDraft Draft, NodePath Path) Insert(WorkflowDraft draft, NodePath parentPath, InsertPosition position, NodeDraft node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!CanInsert(draft, parentPath, position, out var reason))
        {
            throw new EditException(reason);
        }

        var parent = DraftTree.Get(draft, parentPath);
        return position switch
        {
            ChildPosition c => (DraftTree.Replace(draft, parentPath, parent with { Children = parent.Children.Insert(c.Index, node) }), parentPath.Child(c.Index)),
            SlotPosition s => (DraftTree.Replace(draft, parentPath, parent with { Slots = parent.Slots.Add(new SlotEntry(s.Name, node)) }), parentPath.Slot(s.Name)),
            _ => throw new EditException("Unknown position."),
        };
    }

    /// <summary>Removes a node and its subtree. The root cannot be removed.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="path">The node.</param>
    public static WorkflowDraft Remove(WorkflowDraft draft, NodePath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.IsRoot)
        {
            throw new EditException("The root activity cannot be deleted; replace its contents instead.");
        }

        _ = DraftTree.Get(draft, path);
        var parentPath = path.Parent!;
        var parent = DraftTree.Get(draft, parentPath);
        var updated = path.Last switch
        {
            ChildStep c => parent with { Children = parent.Children.RemoveAt(c.Index) },
            SlotStep s => parent with { Slots = parent.Slots.RemoveAll(e => e.Name == s.Name) },
            _ => throw new EditException("Unknown path step."),
        };

        return DraftTree.Replace(draft, parentPath, updated);
    }

    /// <summary>Moves a node (with its subtree) to another place. A node cannot move into itself.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="from">The node to move.</param>
    /// <param name="toParent">The new parent.</param>
    /// <param name="position">The position in the new parent, as seen before the move.</param>
    /// <returns>The new draft and the node's new path.</returns>
    public (WorkflowDraft Draft, NodePath Path) Move(WorkflowDraft draft, NodePath from, NodePath toParent, InsertPosition position)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(toParent);
        ArgumentNullException.ThrowIfNull(position);
        if (from.IsRoot)
        {
            throw new EditException("The root activity cannot be moved.");
        }

        if (from.IsAncestorOrSelfOf(toParent))
        {
            throw new EditException("An activity cannot be moved into itself.");
        }

        var node = DraftTree.Get(draft, from);

        // Moving within the same list: removing the node first shifts later indices by one.
        if (position is ChildPosition target && from.Parent!.Equals(toParent) && from.Last is ChildStep source)
        {
            if (target.Index == source.Index || target.Index == source.Index + 1)
            {
                return (draft, from);
            }

            position = new ChildPosition(target.Index > source.Index ? target.Index - 1 : target.Index);
        }

        var removed = Remove(draft, from);
        var adjustedParent = AdjustAfterRemoval(toParent, from);
        if (!CanInsert(removed, adjustedParent, position, out var reason))
        {
            throw new EditException(reason);
        }

        return Insert(removed, adjustedParent, position, node);
    }

    /// <summary>Sets (or, with null, removes) a property.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="path">The node.</param>
    /// <param name="name">Property name.</param>
    /// <param name="value">New value, or null to remove.</param>
    public static WorkflowDraft SetProperty(WorkflowDraft draft, NodePath path, string name, PropertyDraft? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var node = DraftTree.Get(draft, path);
        var index = node.Properties.FindIndex(p => p.Name == name);
        var properties = (index >= 0, value) switch
        {
            (false, null) => node.Properties,
            (false, not null) => node.Properties.Add(new PropertyEntry(name, value)),
            (true, null) => node.Properties.RemoveAt(index),
            (true, not null) => node.Properties.SetItem(index, new PropertyEntry(name, value)),
        };

        return ReferenceEquals(properties, node.Properties) ? draft : DraftTree.Replace(draft, path, node with { Properties = properties });
    }

    /// <summary>Changes a node's id.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="path">The node.</param>
    /// <param name="id">New id.</param>
    public static WorkflowDraft SetId(WorkflowDraft draft, NodePath path, string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return DraftTree.Replace(draft, path, DraftTree.Get(draft, path) with { Id = id.Trim() });
    }

    /// <summary>Changes (or clears, with null or blank) a node's display name.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="path">The node.</param>
    /// <param name="displayName">New display name.</param>
    public static WorkflowDraft SetDisplayName(WorkflowDraft draft, NodePath path, string? displayName) =>
        DraftTree.Replace(draft, path, DraftTree.Get(draft, path) with { DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName });

    /// <summary>Changes the workflow's identity fields.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="id">Workflow id.</param>
    /// <param name="name">Name.</param>
    /// <param name="version">Version.</param>
    /// <param name="description">Description (blank clears it).</param>
    public static WorkflowDraft SetInfo(WorkflowDraft draft, string id, string name, string version, string? description)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return draft with
        {
            Id = id?.Trim() ?? string.Empty,
            Name = name ?? string.Empty,
            Version = version?.Trim() ?? string.Empty,
            Description = string.IsNullOrWhiteSpace(description) ? null : description,
        };
    }

    /// <summary>Adds or replaces an argument (null index adds).</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="index">Index to replace, or null to add.</param>
    /// <param name="argument">The argument; its default must be valid JSON.</param>
    public static WorkflowDraft SetArgument(WorkflowDraft draft, int? index, ArgumentDraft argument)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(argument);
        CheckDefault(argument.DefaultJson);
        return draft with { Arguments = index is { } i ? draft.Arguments.SetItem(Checked(i, draft.Arguments.Count), argument) : draft.Arguments.Add(argument) };
    }

    /// <summary>Removes an argument.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="index">Index.</param>
    public static WorkflowDraft RemoveArgument(WorkflowDraft draft, int index)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return draft with { Arguments = draft.Arguments.RemoveAt(Checked(index, draft.Arguments.Count)) };
    }

    /// <summary>Adds or replaces a variable (null index adds).</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="index">Index to replace, or null to add.</param>
    /// <param name="variable">The variable; its default must be valid JSON.</param>
    public static WorkflowDraft SetVariable(WorkflowDraft draft, int? index, VariableDraft variable)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(variable);
        CheckDefault(variable.DefaultJson);
        return draft with { Variables = index is { } i ? draft.Variables.SetItem(Checked(i, draft.Variables.Count), variable) : draft.Variables.Add(variable) };
    }

    /// <summary>Removes a variable.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="index">Index.</param>
    public static WorkflowDraft RemoveVariable(WorkflowDraft draft, int index)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return draft with { Variables = draft.Variables.RemoveAt(Checked(index, draft.Variables.Count)) };
    }

    /// <summary>Creates a new, empty node for an activity type with an id that is unique in <paramref name="draft"/>.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="descriptor">The activity type.</param>
    public static NodeDraft CreateNode(WorkflowDraft draft, ActivityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new NodeDraft(NodeIds.Next(NodeIds.InUse(draft), NodeIds.BaseName(descriptor.TypeName.Value)), descriptor.TypeName.Value);
    }

    private static NodePath AdjustAfterRemoval(NodePath path, NodePath removed)
    {
        // If the removed node was an earlier sibling of one of `path`'s ancestors, that ancestor's index moved down by one.
        var parent = removed.Parent!;
        if (removed.Last is not ChildStep removedStep || !parent.IsAncestorOrSelfOf(path) || path.Steps.Count <= parent.Steps.Count)
        {
            return path;
        }

        if (path.Steps[parent.Steps.Count] is ChildStep step && step.Index > removedStep.Index)
        {
            var steps = path.Steps.SetItem(parent.Steps.Count, new ChildStep(step.Index - 1));
            var adjusted = NodePath.Root;
            foreach (var s in steps)
            {
                adjusted = s is ChildStep c ? adjusted.Child(c.Index) : adjusted.Slot(((SlotStep)s).Name);
            }

            return adjusted;
        }

        return path;
    }

    private static void CheckDefault(string? defaultJson)
    {
        if (defaultJson is not null && !DraftJson.IsJsonValue(defaultJson))
        {
            throw new EditException($"The default '{defaultJson}' is not a JSON value (write text in quotes, e.g. \"text\").");
        }
    }

    private static int Checked(int index, int count) =>
        index >= 0 && index < count ? index : throw new EditException(string.Create(CultureInfo.InvariantCulture, $"There is no row {index}."));
}

/// <summary>Node id generation.</summary>
public static class NodeIds
{
    /// <summary>The ids used in a draft.</summary>
    /// <param name="draft">The draft.</param>
    public static HashSet<string> InUse(WorkflowDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return draft.Root.DescendantsAndSelf().Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>A readable base for new ids: the lower-case last segment of the type name (<c>Core.Log</c> → <c>log</c>).</summary>
    /// <param name="typeName">Activity type name.</param>
    public static string BaseName(string typeName)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        var last = typeName[(typeName.LastIndexOf('.') + 1)..];
        return last.Length == 0 ? "node" : last.ToLowerInvariant();
    }

    /// <summary>Returns <c>base-N</c> with the smallest N ≥ 1 not in <paramref name="used"/>, and reserves it.</summary>
    /// <param name="used">Ids in use (updated).</param>
    /// <param name="baseName">Base name.</param>
    public static string Next(HashSet<string> used, string baseName)
    {
        ArgumentNullException.ThrowIfNull(used);
        for (var n = 1; ; n++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{baseName}-{n}");
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
