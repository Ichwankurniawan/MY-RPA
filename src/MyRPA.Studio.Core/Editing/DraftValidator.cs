using System.Globalization;
using System.Text.RegularExpressions;
using MyRPA.Core.Activities;
using MyRPA.Studio.Documents;
using MyRPA.Workflow;
using MyRPA.Workflow.Validation;

namespace MyRPA.Studio.Editing;

/// <summary>Where a validation diagnostic belongs in the designer.</summary>
/// <param name="Diagnostic">The engine's diagnostic (code, severity, message, JSON path).</param>
/// <param name="Node">The node, when the diagnostic is about a node or one of its properties.</param>
/// <param name="Property">The property, when the diagnostic is about one.</param>
/// <param name="ArgumentIndex">The argument row, when the diagnostic is about an argument.</param>
/// <param name="VariableIndex">The variable row, when the diagnostic is about a variable.</param>
public sealed record DraftDiagnostic(ValidationDiagnostic Diagnostic, NodePath? Node, string? Property, int? ArgumentIndex, int? VariableIndex)
{
    /// <summary>Whether the diagnostic is an error (rather than a warning).</summary>
    public bool IsError => Diagnostic.Severity == DiagnosticSeverity.Error;
}

/// <summary>The outcome of validating a draft.</summary>
/// <param name="Diagnostics">Every diagnostic, located.</param>
/// <param name="Workflow">The executable definition when there are no errors.</param>
/// <param name="Json">The JSON that was validated (what Save writes).</param>
public sealed record DraftValidation(IReadOnlyList<DraftDiagnostic> Diagnostics, WorkflowDefinition? Workflow, string Json)
{
    /// <summary>Whether the draft is a valid, runnable workflow.</summary>
    public bool IsValid => Workflow is not null;
}

/// <summary>
/// Validates drafts with the engine's own pipeline (ADR-0018): the draft is written as workflow JSON and loaded by
/// <see cref="WorkflowLoader"/>, exactly like <c>myrpa validate</c>, so the designer can never disagree with the CLI or
/// the runtime. Diagnostics are mapped back through the node paths recorded while writing (longest matching prefix),
/// which is exact even for slot names that contain dots.
/// </summary>
/// <param name="catalog">Registered activities.</param>
public sealed partial class DraftValidator(IActivityCatalog catalog)
{
    private readonly WorkflowLoader _loader = new(catalog);

    /// <summary>Validates a draft.</summary>
    /// <param name="draft">The draft.</param>
    public DraftValidation Validate(WorkflowDraft draft)
    {
        var json = DraftJson.Write(draft, out var nodePaths);
        var result = _loader.Load(json);
        var located = result.Diagnostics.Select(d => Locate(d, nodePaths)).ToList();
        return new DraftValidation(located, result.IsValid ? result.Workflow : null, json);
    }

    private static DraftDiagnostic Locate(ValidationDiagnostic diagnostic, IReadOnlyDictionary<string, NodePath> nodePaths)
    {
        var path = diagnostic.Path;
        if (ArgumentOrVariable().Match(path) is { Success: true } row)
        {
            var index = int.Parse(row.Groups["index"].ValueSpan, CultureInfo.InvariantCulture);
            return row.Groups["list"].Value == "arguments"
                ? new DraftDiagnostic(diagnostic, null, null, index, null)
                : new DraftDiagnostic(diagnostic, null, null, null, index);
        }

        // The longest node path that is a prefix of the diagnostic path and leaves a remainder a node can have
        // (".properties…", ".type", …). Checking the remainder keeps slot names that contain dots exact.
        string? best = null;
        string? fallback = null;
        foreach (var candidate in nodePaths.Keys)
        {
            if (path != candidate && !path.StartsWith(candidate + ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (fallback is null || candidate.Length > fallback.Length)
            {
                fallback = candidate;
            }

            if (IsNodeRemainder(path[candidate.Length..]) && (best is null || candidate.Length > best.Length))
            {
                best = candidate;
            }
        }

        best ??= fallback; // e.g. a warning about an unknown field of a node

        if (best is null)
        {
            return new DraftDiagnostic(diagnostic, null, null, null, null);
        }

        string? property = null;
        var rest = path[best.Length..];
        if (rest.StartsWith(".properties.", StringComparison.Ordinal))
        {
            var name = rest[".properties.".Length..];
            var dot = name.IndexOf('.', StringComparison.Ordinal);
            property = dot < 0 ? name : name[..dot];
        }

        return new DraftDiagnostic(diagnostic, nodePaths[best], property, null, null);
    }

    private static bool IsNodeRemainder(string rest) =>
        rest.Length == 0
        || rest is ".id" or ".type" or ".displayName" or ".properties" or ".children" or ".slots"
        || rest.StartsWith(".properties.", StringComparison.Ordinal)
        || rest.StartsWith(".children[", StringComparison.Ordinal)
        || rest.StartsWith(".slots.", StringComparison.Ordinal);

    [GeneratedRegex(@"^\$\.(?<list>arguments|variables)\[(?<index>\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex ArgumentOrVariable();
}
