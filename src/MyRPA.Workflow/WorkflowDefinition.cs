using MyRPA.Core.Identifiers;

namespace MyRPA.Workflow;

/// <summary>
/// A workflow definition: the one model shared by every MyRPA client (ADR-0002). Immutable.
/// </summary>
/// <remarks>
/// Two versions are tracked on purpose: <see cref="SchemaVersion"/> is the file-format version (drives
/// migrations); <see cref="Version"/> is the author's version of this workflow's content.
/// Phase 1 skeleton: arguments, variables and node properties are added in Phase 2.
/// </remarks>
public sealed class WorkflowDefinition
{
    /// <summary>Creates a workflow definition.</summary>
    /// <param name="id">Stable workflow identifier.</param>
    /// <param name="name">Human-readable name.</param>
    /// <param name="version">Content version chosen by the author, e.g. "1.0.0".</param>
    /// <param name="root">The root node.</param>
    /// <param name="schemaVersion">File-format version; defaults to <see cref="WorkflowSchemaVersion.Current"/>.</param>
    /// <exception cref="ArgumentException">Node identifiers are not unique.</exception>
    public WorkflowDefinition(
        WorkflowId id,
        string name,
        string version,
        NodeDefinition root,
        WorkflowSchemaVersion? schemaVersion = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(root);

        var duplicate = root.DescendantsAndSelf()
            .GroupBy(n => n.Id)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Node id '{duplicate.Key}' is used more than once.", nameof(root));
        }

        Id = id;
        Name = name;
        Version = version;
        Root = root;
        SchemaVersion = schemaVersion ?? WorkflowSchemaVersion.Current;
    }

    /// <summary>Stable workflow identifier.</summary>
    public WorkflowId Id { get; }

    /// <summary>Human-readable name.</summary>
    public string Name { get; }

    /// <summary>Content version chosen by the author.</summary>
    public string Version { get; }

    /// <summary>File-format version.</summary>
    public WorkflowSchemaVersion SchemaVersion { get; }

    /// <summary>The root node.</summary>
    public NodeDefinition Root { get; }
}
