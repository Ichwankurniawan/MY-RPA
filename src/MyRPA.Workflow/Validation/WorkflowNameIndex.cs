using MyRPA.Workflow.Values;

namespace MyRPA.Workflow.Validation;

/// <summary>What a declared name is (ADR-0041).</summary>
public enum WorkflowSymbolKind
{
    /// <summary>A workflow argument.</summary>
    Argument = 0,

    /// <summary>A workflow variable.</summary>
    Variable = 1,

    /// <summary>A read-only local declared by an activity for some of its slots (a ForEach item, a caught exception).</summary>
    Local = 2,
}

/// <summary>A declared name: an argument, a variable or a local.</summary>
public sealed class WorkflowSymbol
{
    internal WorkflowSymbol(string name, WorkflowSymbolKind kind, WorkflowDataType type, string declarationPath, ArgumentDirection? direction = null)
    {
        Name = name;
        Kind = kind;
        Type = type;
        DeclarationPath = declarationPath;
        Direction = direction;
    }

    /// <summary>The name.</summary>
    public string Name { get; }

    /// <summary>Argument, variable or local.</summary>
    public WorkflowSymbolKind Kind { get; }

    /// <summary>The declared type (<see cref="WorkflowDataType.Object"/> for locals).</summary>
    public WorkflowDataType Type { get; }

    /// <summary>The JSON path of the declaration: the argument or variable entry, or the activity property naming the local.</summary>
    public string DeclarationPath { get; }

    /// <summary>The direction of an argument; <see langword="null"/> for variables and locals.</summary>
    public ArgumentDirection? Direction { get; }
}

/// <summary>
/// A place where a name is written in the document (ADR-0041): the JSON path of a string value (the diagnostics' path
/// form) and the exact characters of the name inside it.
/// </summary>
/// <param name="Path">The JSON path of the string: an expression, an assignment target, a name map entry or a declaration's name.</param>
/// <param name="Start">The name's offset in that string (0-based).</param>
/// <param name="Length">The name's length.</param>
/// <param name="IsDeclaration">Whether this is where the name is declared.</param>
public sealed record WorkflowNameReference(string Path, int Start, int Length, bool IsDeclaration);

/// <summary>
/// The names of a workflow document as the loader's semantic validation sees them (ADR-0041): what is declared, what is
/// in scope at each activity, and where each declaration is used. Built by <see cref="WorkflowLoader.IndexNames"/>, also
/// for documents with errors (as far as they can be read). Names inside an expression that does not parse are not known.
/// </summary>
public sealed class WorkflowNameIndex
{
    /// <summary>The scope key of workflow-level paths (arguments, variables, metadata).</summary>
    internal const string WorkflowLevel = "$";

    private readonly Dictionary<string, IReadOnlyList<WorkflowSymbol>> _scopes;
    private readonly List<(WorkflowSymbol Symbol, WorkflowNameReference Reference)> _references;

    internal WorkflowNameIndex(
        List<WorkflowSymbol> symbols,
        Dictionary<string, IReadOnlyList<WorkflowSymbol>> scopes,
        List<(WorkflowSymbol Symbol, WorkflowNameReference Reference)> references)
    {
        Symbols = symbols;
        _scopes = scopes;
        _references = references;
    }

    /// <summary>The index of a document that could not be read: no names.</summary>
    public static WorkflowNameIndex Empty { get; } = new([], [], []);

    /// <summary>Every declaration, in document order.</summary>
    public IReadOnlyList<WorkflowSymbol> Symbols { get; }

    /// <summary>
    /// The names visible at <paramref name="path"/>: a node's path or any path inside it (a property, a transition). At
    /// workflow level (an argument, a variable) these are the arguments and variables. Arguments first, then variables,
    /// then locals from the outermost in.
    /// </summary>
    /// <param name="path">A JSON path in the diagnostics' form, e.g. <c>$.root.children[1].properties.message</c>.</param>
    /// <returns>The visible declarations.</returns>
    public IReadOnlyList<WorkflowSymbol> InScope(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string? best = null;
        foreach (var key in _scopes.Keys)
        {
            if ((path == key || path.StartsWith(key + ".", StringComparison.Ordinal)) && (best is null || key.Length > best.Length))
            {
                best = key;
            }
        }

        return best is not null ? _scopes[best] : [];
    }

    /// <summary>The declaration <paramref name="name"/> resolves to at <paramref name="path"/>, if any.</summary>
    /// <param name="path">Where the name is used (see <see cref="InScope"/>).</param>
    /// <param name="name">The name.</param>
    /// <returns>The declaration, or <see langword="null"/> when the name is not in scope there.</returns>
    public WorkflowSymbol? Resolve(string path, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return InScope(path).LastOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
    }

    /// <summary>Where <paramref name="symbol"/> is declared and used, in document order.</summary>
    /// <param name="symbol">A declaration of this index.</param>
    /// <returns>Its declaration and its uses.</returns>
    public IReadOnlyList<WorkflowNameReference> ReferencesTo(WorkflowSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return [.. _references.Where(r => ReferenceEquals(r.Symbol, symbol)).Select(r => r.Reference)];
    }
}

/// <summary>Collects a <see cref="WorkflowNameIndex"/> while the semantic validator walks a document.</summary>
internal sealed class WorkflowNameRecorder
{
    private readonly List<WorkflowSymbol> _symbols = [];
    private readonly Dictionary<string, IReadOnlyList<WorkflowSymbol>> _scopes = new(StringComparer.Ordinal);
    private readonly List<(WorkflowSymbol, WorkflowNameReference)> _references = [];

    public void Declared(WorkflowSymbol symbol, string path)
    {
        _symbols.Add(symbol);
        _references.Add((symbol, new WorkflowNameReference(path, 0, symbol.Name.Length, IsDeclaration: true)));
    }

    public void Scope(string path, IReadOnlyList<WorkflowSymbol> visible) => _scopes[path] = visible;

    public void Used(WorkflowSymbol symbol, string path, int start, int length) =>
        _references.Add((symbol, new WorkflowNameReference(path, start, length, IsDeclaration: false)));

    public WorkflowNameIndex Build() => new(_symbols, _scopes, [.. _references]);
}
