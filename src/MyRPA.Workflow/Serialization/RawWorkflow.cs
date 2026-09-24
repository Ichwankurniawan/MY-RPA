using System.Text.Json;

namespace MyRPA.Workflow.Serialization;

/// <summary>
/// Untrusted, structurally read workflow document (stage 2 of ADR-0011). Every member may be missing; paths are
/// kept for diagnostics. Elements reference the source <see cref="JsonDocument"/>, which must outlive this object.
/// </summary>
internal sealed class RawWorkflow
{
    public string? SchemaVersion { get; set; }

    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Version { get; set; }

    public string? Description { get; set; }

    public List<RawArgument> Arguments { get; } = [];

    public List<RawVariable> Variables { get; } = [];

    public RawNode? Root { get; set; }
}

internal sealed class RawArgument(string path)
{
    public string Path { get; } = path;

    public string? Name { get; set; }

    public string? Direction { get; set; }

    public string? Type { get; set; }

    public bool Required { get; set; }

    public JsonElement? Default { get; set; }
}

internal sealed class RawVariable(string path)
{
    public string Path { get; } = path;

    public string? Name { get; set; }

    public string? Type { get; set; }

    public JsonElement? Default { get; set; }
}

internal sealed class RawNode(string path)
{
    public string Path { get; } = path;

    public string? Id { get; set; }

    public string? Type { get; set; }

    public string? DisplayName { get; set; }

    public List<RawProperty> Properties { get; } = [];

    public List<RawNode> Children { get; } = [];

    public List<KeyValuePair<string, RawNode>> Slots { get; } = [];
}

internal sealed record RawProperty(string Name, JsonElement Value, string Path);
