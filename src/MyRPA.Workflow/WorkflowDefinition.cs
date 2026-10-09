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
    /// <param name="schemaVersion">
    /// File-format version; defaults to <see cref="WorkflowSchemaVersion.Graphs"/> when a node has transitions or a layout,
    /// otherwise <see cref="WorkflowSchemaVersion.Initial"/>.
    /// </param>
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
        // Files without graph data stay 1.0 (format §8); transitions and layout need 1.1 (ADR-0037).
        var usesGraphData = root.DescendantsAndSelf().Any(n => n.Transitions.Count > 0 || n.Layout is not null);
        if (usesGraphData && schemaVersion is not null && schemaVersion < WorkflowSchemaVersion.Graphs)
        {
            throw new ArgumentException($"Transitions and layout need schema version {WorkflowSchemaVersion.Graphs} or later.", nameof(schemaVersion));
        }

        SchemaVersion = schemaVersion ?? (usesGraphData ? WorkflowSchemaVersion.Graphs : WorkflowSchemaVersion.Initial);
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

    /// <summary>
    /// Names (arguments, variables, locals) that feed a secret property (ADR-0043): set by the loader, which knows the
    /// catalog. A debugger shows their values masked. Empty for a workflow built without the loader.
    /// </summary>
    public IReadOnlySet<string> SecretNames { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}
