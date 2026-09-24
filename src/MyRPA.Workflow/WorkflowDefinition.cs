using MyRPA.Core.Identifiers;

namespace MyRPA.Workflow;

/// <summary>
/// A workflow definition: the one model shared by every MyRPA client (ADR-0002). Immutable.
/// </summary>
/// <remarks>
/// <see cref="SchemaVersion"/> is the file-format version (drives migrations); <see cref="Version"/> is the author's
/// version of this workflow's content. Constructors enforce invariants only (non-null parts, unique node ids, unique
/// argument/variable names). Loading user files goes through <see cref="Validation.WorkflowLoader"/>, which reports
/// every problem instead of throwing (ADR-0011).
/// </remarks>
public sealed class WorkflowDefinition
{
    /// <summary>Creates a workflow definition.</summary>
    /// <param name="id">Stable workflow identifier.</param>
    /// <param name="name">Human-readable name.</param>
    /// <param name="version">Content version chosen by the author, e.g. "1.0.0".</param>
    /// <param name="root">The root node.</param>
    /// <param name="schemaVersion">File-format version; defaults to <see cref="WorkflowSchemaVersion.Current"/>.</param>
    /// <param name="arguments">Declared arguments.</param>
    /// <param name="variables">Declared variables.</param>
    /// <param name="description">Optional description.</param>
    /// <exception cref="ArgumentException">Node ids or argument/variable names are not unique.</exception>
    public WorkflowDefinition(
        WorkflowId id,
        string name,
        string version,
        NodeDefinition root,
        WorkflowSchemaVersion? schemaVersion = null,
        IEnumerable<ArgumentDefinition>? arguments = null,
        IEnumerable<VariableDefinition>? variables = null,
        string? description = null)
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

        Arguments = [.. arguments ?? []];
        Variables = [.. variables ?? []];
        var duplicateName = Arguments.Select(a => a.Name).Concat(Variables.Select(v => v.Name))
            .GroupBy(n => n, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateName is not null)
        {
            throw new ArgumentException($"Name '{duplicateName.Key}' is declared more than once.", nameof(arguments));
        }

        Id = id;
        Name = name;
        Version = version;
        Root = root;
        SchemaVersion = schemaVersion ?? WorkflowSchemaVersion.Current;
        Description = description;
    }

    /// <summary>Stable workflow identifier.</summary>
    public WorkflowId Id { get; }

    /// <summary>Human-readable name.</summary>
    public string Name { get; }

    /// <summary>Content version chosen by the author.</summary>
    public string Version { get; }

    /// <summary>File-format version.</summary>
    public WorkflowSchemaVersion SchemaVersion { get; }

    /// <summary>Optional description.</summary>
    public string? Description { get; }

    /// <summary>Declared arguments.</summary>
    public IReadOnlyList<ArgumentDefinition> Arguments { get; }

    /// <summary>Declared variables.</summary>
    public IReadOnlyList<VariableDefinition> Variables { get; }

    /// <summary>The root node.</summary>
    public NodeDefinition Root { get; }
}
