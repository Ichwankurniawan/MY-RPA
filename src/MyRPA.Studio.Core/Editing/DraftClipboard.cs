using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MyRPA.Studio.Documents;

namespace MyRPA.Studio.Editing;

/// <summary>
/// Clipboard format for activities: <c>{ "myrpaNodes": "1.0", "nodes": [ node, … ] }</c> where each node is written in
/// the workflow JSON format, so copied activities are readable text. Pasting gives every node whose id is already
/// used (in the document or earlier in the pasted set) a new unique id, so paste never creates duplicate ids.
/// </summary>
public static class DraftClipboard
{
    /// <summary>The clipboard format marker.</summary>
    public const string Marker = "myrpaNodes";

    /// <summary>Serializes nodes for the clipboard.</summary>
    /// <param name="nodes">The nodes (with their subtrees).</param>
    public static string Serialize(IEnumerable<NodeDraft> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString(Marker, "1.0");
            writer.WriteStartArray("nodes");
            foreach (var node in nodes)
            {
                writer.WriteRawValue(DraftJson.WriteNode(node));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Reads nodes from clipboard text; returns false when the text is not MyRPA activities.</summary>
    /// <param name="text">Clipboard text.</param>
    /// <param name="nodes">The nodes.</param>
    public static bool TryDeserialize(string? text, out IReadOnlyList<NodeDraft> nodes)
    {
        nodes = [];
        if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(Marker, out _) || !root.TryGetProperty("nodes", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            nodes = [.. array.EnumerateArray().Select((n, i) => DraftJson.ReadNode(n, $"$.nodes[{i}]"))];
            return nodes.Count > 0;
        }
        catch (Exception ex) when (ex is JsonException or DraftReadException)
        {
            return false;
        }
    }

    /// <summary>Gives nodes (and their descendants) new ids where the id is already used in <paramref name="draft"/>.</summary>
    /// <param name="nodes">Nodes to paste.</param>
    /// <param name="draft">The document they are pasted into.</param>
    public static IReadOnlyList<NodeDraft> PrepareForPaste(IEnumerable<NodeDraft> nodes, WorkflowDraft draft)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var used = NodeIds.InUse(draft);
        return [.. nodes.Select(n => Rename(n, used))];
    }

    private static NodeDraft Rename(NodeDraft node, HashSet<string> used)
    {
        var id = node.Id.Length > 0 && used.Add(node.Id) ? node.Id : NodeIds.Next(used, NodeIds.BaseName(node.Type));
        return node with
        {
            Id = id,
            Children = [.. node.Children.Select(c => Rename(c, used))],
            Slots = [.. node.Slots.Select(s => s with { Node = Rename(s.Node, used) })],
        };
    }
}
