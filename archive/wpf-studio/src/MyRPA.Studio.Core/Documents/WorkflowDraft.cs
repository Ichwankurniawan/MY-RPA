using System.Collections.Immutable;

namespace MyRPA.Studio.Documents;

/// <summary>
/// The editable form of a workflow (ADR-0018): an immutable tree with the same shape as the JSON format that, unlike
/// <c>WorkflowDefinition</c>, can hold unfinished work — empty or invalid expressions, missing required properties,
/// unknown activity types, duplicate names. Every edit produces a new draft (so undo is a snapshot); validation and
/// execution always go through the JSON format and the engine's <c>WorkflowLoader</c>.
/// </summary>
public sealed record WorkflowDraft
{
    /// <summary>Creates a draft.</summary>
    /// <param name="root">Root node.</param>
    public WorkflowDraft(NodeDraft root)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
    }

    /// <summary>Schema version text, e.g. <c>1.0</c>.</summary>
    public string SchemaVersion { get; init; } = "1.0";

    /// <summary>Workflow id.</summary>
    public string Id { get; init; } = "new-workflow";

    /// <summary>Display name.</summary>
    public string Name { get; init; } = "New workflow";

    /// <summary>Workflow version text.</summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>Arguments in declaration order.</summary>
    public ImmutableList<ArgumentDraft> Arguments { get; init; } = [];

    /// <summary>Variables in declaration order.</summary>
    public ImmutableList<VariableDraft> Variables { get; init; } = [];

    /// <summary>The root node.</summary>
    public NodeDraft Root { get; init; }

    /// <summary>Fields this build does not know, kept verbatim so saving never loses them.</summary>
    public ImmutableList<JsonField> Extra { get; init; } = [];

    /// <summary>A new, empty workflow with a <c>Core.Sequence</c> root.</summary>
    public static WorkflowDraft CreateNew() => new(new NodeDraft("main", "Core.Sequence"));
}

/// <summary>An argument as written in the file (type and direction are text, so invalid values can be edited).</summary>
/// <param name="Name">Name.</param>
/// <param name="Direction"><c>In</c>, <c>Out</c> or <c>InOut</c>.</param>
/// <param name="Type">Data type name.</param>
/// <param name="Required">Whether an In argument must be supplied.</param>
/// <param name="DefaultJson">The default as raw JSON, or null.</param>
public sealed record ArgumentDraft(string Name, string Direction, string Type, bool Required = false, string? DefaultJson = null);

/// <summary>A variable as written in the file.</summary>
/// <param name="Name">Name.</param>
/// <param name="Type">Data type name.</param>
/// <param name="DefaultJson">The default as raw JSON, or null.</param>
public sealed record VariableDraft(string Name, string Type, string? DefaultJson = null);

/// <summary>A node: id, activity type, properties, children and slots.</summary>
/// <param name="Id">Node id (unique within a valid workflow).</param>
/// <param name="Type">Activity type name.</param>
public sealed record NodeDraft(string Id, string Type)
{
    /// <summary>Optional display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Properties in file order.</summary>
    public ImmutableList<PropertyEntry> Properties { get; init; } = [];

    /// <summary>Children in order.</summary>
    public ImmutableList<NodeDraft> Children { get; init; } = [];

    /// <summary>Slots in file order.</summary>
    public ImmutableList<SlotEntry> Slots { get; init; } = [];

    /// <summary>Unknown node fields, kept verbatim.</summary>
    public ImmutableList<JsonField> Extra { get; init; } = [];

    /// <summary>Returns a property value, or null.</summary>
    /// <param name="name">Property name.</param>
    public PropertyDraft? Property(string name) => Properties.FirstOrDefault(p => p.Name == name)?.Value;

    /// <summary>Returns the node in a slot, or null.</summary>
    /// <param name="name">Slot name.</param>
    public NodeDraft? Slot(string name) => Slots.FirstOrDefault(s => s.Name == name)?.Node;

    /// <summary>This node and all nodes below it, depth-first.</summary>
    public IEnumerable<NodeDraft> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children.Concat(Slots.Select(s => s.Node)))
        {
            foreach (var node in child.DescendantsAndSelf())
            {
                yield return node;
            }
        }
    }
}

/// <summary>A named property value.</summary>
/// <param name="Name">Property name.</param>
/// <param name="Value">Value.</param>
public sealed record PropertyEntry(string Name, PropertyDraft Value);

/// <summary>A named slot with its node.</summary>
/// <param name="Name">Slot name (e.g. <c>then</c>, <c>case:gold</c>).</param>
/// <param name="Node">The node in the slot.</param>
public sealed record SlotEntry(string Name, NodeDraft Node);

/// <summary>An unknown JSON field kept verbatim.</summary>
/// <param name="Name">Field name.</param>
/// <param name="RawJson">Raw JSON value.</param>
public sealed record JsonField(string Name, string RawJson);

/// <summary>A property value as written in the file.</summary>
public abstract record PropertyDraft;

/// <summary>
/// A single value: text written as a JSON string (an expression, text or name), or a raw JSON literal such as
/// <c>500</c> or <c>true</c> (a literal expression).
/// </summary>
/// <param name="Text">The text, or the raw literal.</param>
/// <param name="IsJsonLiteral">Whether <paramref name="Text"/> is written unquoted.</param>
public sealed record ScalarValue(string Text, bool IsJsonLiteral = false) : PropertyDraft;

/// <summary>A map property (expression map or assignment-target map) in file order.</summary>
/// <param name="Entries">Key/value entries.</param>
public sealed record MapValue(ImmutableList<MapEntry> Entries) : PropertyDraft;

/// <summary>One entry of a <see cref="MapValue"/>.</summary>
/// <param name="Key">Key.</param>
/// <param name="Value">Value.</param>
public sealed record MapEntry(string Key, ScalarValue Value);
