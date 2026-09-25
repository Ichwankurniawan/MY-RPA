using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MyRPA.Core.Activities;
using MyRPA.Studio.Documents;
using MyRPA.Studio.Editing;

namespace MyRPA.Studio.ViewModels;

/// <summary>What is being dragged: a new activity from the toolbox, or an existing node.</summary>
public abstract record DragPayload;

/// <summary>A new activity of type <paramref name="TypeName"/>.</summary>
/// <param name="TypeName">Activity type.</param>
public sealed record NewActivityPayload(ActivityTypeName TypeName) : DragPayload;

/// <summary>The existing node at <paramref name="Path"/>.</summary>
/// <param name="Path">Its path.</param>
public sealed record MoveNodePayload(NodePath Path) : DragPayload;

/// <summary>How a node took part in the last run.</summary>
public enum NodeRunState
{
    /// <summary>Not run (or no run yet).</summary>
    None = 0,

    /// <summary>Executing now.</summary>
    Running = 1,

    /// <summary>Executed.</summary>
    Completed = 2,

    /// <summary>The node where the run failed.</summary>
    Failed = 3,
}

/// <summary>
/// One activity block in the designer (structured nested blocks, ADR-0018). Built from the draft after every change;
/// children and slots are rendered generically from the activity's descriptor, so plugin activities need no custom
/// designer.
/// </summary>
public sealed partial class NodeViewModel : ObservableObject
{
    internal NodeViewModel(NodePath path, NodeDraft node, ActivityDescriptor? descriptor)
    {
        Path = path;
        Id = node.Id;
        TypeName = node.Type;
        Descriptor = descriptor;
        Title = node.DisplayName ?? descriptor?.DisplayName ?? node.Type;
        Summary = BuildSummary(node, descriptor);
        Category = descriptor?.Category ?? "Unknown";
        IsKnown = descriptor is not null;
        Payload = new MoveNodePayload(path);
    }

    /// <summary>The node's path.</summary>
    public NodePath Path { get; }

    /// <summary>Node id.</summary>
    public string Id { get; }

    /// <summary>Activity type name.</summary>
    public string TypeName { get; }

    /// <summary>The activity's metadata, or null for unregistered types.</summary>
    public ActivityDescriptor? Descriptor { get; }

    /// <summary>Display name (or the activity's display name).</summary>
    public string Title { get; }

    /// <summary>A short preview of the node's main property.</summary>
    public string Summary { get; }

    /// <summary>Activity category.</summary>
    public string Category { get; }

    /// <summary>Whether the activity type is registered.</summary>
    public bool IsKnown { get; }

    /// <summary>The drag payload for moving this node.</summary>
    public DragPayload Payload { get; }

    /// <summary>Drop zones and child nodes, interleaved (zone, child, zone, …, zone); empty when the activity has no list.</summary>
    public ObservableCollection<object> Items { get; } = [];

    /// <summary>Slots (declared slots, existing prefix slots, and "add case" zones for prefix slots).</summary>
    public ObservableCollection<SlotViewModel> Slots { get; } = [];

    /// <summary>Whether the activity has a list of children.</summary>
    public bool HasList => Descriptor?.AllowsChildren == true;

    /// <summary>Whether the activity has slots.</summary>
    public bool HasSlots => Slots.Count > 0;

    /// <summary>Whether validation reported errors on this node or its properties.</summary>
    [ObservableProperty]
    public partial bool HasErrors { get; set; }

    /// <summary>The validation messages for this node (tooltip).</summary>
    [ObservableProperty]
    public partial string ErrorText { get; set; } = string.Empty;

    /// <summary>Whether the node is selected.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>The node's state in the last run.</summary>
    [ObservableProperty]
    public partial NodeRunState RunState { get; set; }

    /// <summary>This node and all nodes below it.</summary>
    public IEnumerable<NodeViewModel> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Items.OfType<NodeViewModel>().Concat(Slots.Where(s => s.Node is not null).Select(s => s.Node!)))
        {
            foreach (var node in child.DescendantsAndSelf())
            {
                yield return node;
            }
        }
    }

    /// <summary>Builds the view model tree for a draft.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="catalog">Registered activities.</param>
    public static NodeViewModel Build(WorkflowDraft draft, IActivityCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(catalog);
        return Build(draft.Root, NodePath.Root, catalog);
    }

    private static NodeViewModel Build(NodeDraft node, NodePath path, IActivityCatalog catalog)
    {
        var descriptor = ActivityTypeName.TryCreate(node.Type, out var type) && catalog.TryGet(type, out var found) ? found : null;
        var vm = new NodeViewModel(path, node, descriptor);

        if (descriptor?.AllowsChildren == true || (descriptor is null && node.Children.Count > 0))
        {
            for (var i = 0; i < node.Children.Count; i++)
            {
                if (descriptor is not null)
                {
                    vm.Items.Add(new DropZoneViewModel(path, new ChildPosition(i), string.Empty));
                }

                vm.Items.Add(Build(node.Children[i], path.Child(i), catalog));
            }

            if (descriptor is not null)
            {
                vm.Items.Add(new DropZoneViewModel(path, new ChildPosition(node.Children.Count), node.Children.Count == 0 ? "Drop activities here" : string.Empty));
            }
        }

        var handled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in descriptor?.Slots ?? [])
        {
            if (slot.IsPrefix)
            {
                foreach (var entry in node.Slots.Where(s => slot.Accepts(s.Name)))
                {
                    handled.Add(entry.Name);
                    vm.Slots.Add(new SlotViewModel(entry.Name, entry.Name, Build(entry.Node, path.Slot(entry.Name), catalog), null));
                }

                vm.Slots.Add(new SlotViewModel(slot.Name, $"+ {slot.Name}…", null, new DropZoneViewModel(path, new SlotPosition(slot.Name), $"Drop to add a {slot.Name.TrimEnd(':')}", needsSlotName: true)));
            }
            else
            {
                handled.Add(slot.Name);
                var existing = node.Slot(slot.Name);
                vm.Slots.Add(existing is null
                    ? new SlotViewModel(slot.Name, slot.Name, null, new DropZoneViewModel(path, new SlotPosition(slot.Name), slot.IsRequired ? "Required: drop an activity" : "Drop an activity"))
                    : new SlotViewModel(slot.Name, slot.Name, Build(existing, path.Slot(slot.Name), catalog), null));
            }
        }

        // Slots the descriptor does not declare (or of an unknown activity) are still shown, so nothing is hidden.
        foreach (var entry in node.Slots.Where(s => !handled.Contains(s.Name)))
        {
            vm.Slots.Add(new SlotViewModel(entry.Name, entry.Name + " (unknown slot)", Build(entry.Node, path.Slot(entry.Name), catalog), null));
        }

        return vm;
    }

    private static string BuildSummary(NodeDraft node, ActivityDescriptor? descriptor)
    {
        var property = descriptor?.Properties.FirstOrDefault(p => p.IsRequired && node.Property(p.Name) is ScalarValue)
            ?? descriptor?.Properties.FirstOrDefault(p => node.Property(p.Name) is ScalarValue);
        if (property is null || node.Property(property.Name) is not ScalarValue value)
        {
            return string.Empty;
        }

        var text = value.Text.ReplaceLineEndings(" ");
        return $"{property.Name}: {(text.Length > 60 ? text[..57] + "…" : text)}";
    }
}

/// <summary>A slot of a node: its node, or an empty drop zone.</summary>
/// <param name="name">Slot name (for prefix placeholders, the prefix).</param>
/// <param name="label">Label shown in the designer.</param>
/// <param name="node">The node in the slot.</param>
/// <param name="emptyZone">The drop zone when the slot is empty (or the "add" zone of a prefix slot).</param>
public sealed class SlotViewModel(string name, string label, NodeViewModel? node, DropZoneViewModel? emptyZone)
{
    /// <summary>Slot name.</summary>
    public string Name { get; } = name;

    /// <summary>Label.</summary>
    public string Label { get; } = label;

    /// <summary>The node, when the slot is filled.</summary>
    public NodeViewModel? Node { get; } = node;

    /// <summary>The drop zone, when the slot is empty.</summary>
    public DropZoneViewModel? EmptyZone { get; } = emptyZone;
}

/// <summary>A place where an activity can be dropped (between children, or into an empty slot).</summary>
public sealed partial class DropZoneViewModel : ObservableObject
{
    /// <summary>Creates the zone.</summary>
    /// <param name="parentPath">The parent node.</param>
    /// <param name="position">Where in the parent.</param>
    /// <param name="hint">Text shown in the zone (empty for the thin zones between children).</param>
    /// <param name="needsSlotName">Whether dropping asks for a slot name (Switch cases).</param>
    public DropZoneViewModel(NodePath parentPath, InsertPosition position, string hint, bool needsSlotName = false)
    {
        ParentPath = parentPath;
        Position = position;
        Hint = hint;
        NeedsSlotName = needsSlotName;
    }

    /// <summary>The parent node.</summary>
    public NodePath ParentPath { get; }

    /// <summary>Where in the parent.</summary>
    public InsertPosition Position { get; }

    /// <summary>Text shown in the zone.</summary>
    public string Hint { get; }

    /// <summary>Whether the zone shows a hint (a larger target).</summary>
    public bool HasHint => Hint.Length > 0;

    /// <summary>Whether dropping asks for a slot name.</summary>
    public bool NeedsSlotName { get; }

    /// <summary>Whether a drag is over the zone and would be accepted.</summary>
    [ObservableProperty]
    public partial bool IsHighlighted { get; set; }
}
