using System.Diagnostics.CodeAnalysis;

namespace MyRPA.Workflow.Validation;

/// <summary>Result of loading and validating workflow JSON (ADR-0011).</summary>
public sealed class WorkflowLoadResult
{
    internal WorkflowLoadResult(WorkflowDefinition? workflow, IReadOnlyList<ValidationDiagnostic> diagnostics)
    {
        Diagnostics = diagnostics;
        Workflow = HasErrors ? null : workflow;
    }

    /// <summary>The validated workflow; <see langword="null"/> when there are errors.</summary>
    public WorkflowDefinition? Workflow { get; }

    /// <summary>All diagnostics (errors and warnings), in document order.</summary>
    public IReadOnlyList<ValidationDiagnostic> Diagnostics { get; }

    /// <summary>Whether any diagnostic is an error.</summary>
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>Whether the workflow is valid (no errors; warnings allowed).</summary>
    [MemberNotNullWhen(true, nameof(Workflow))]
    public bool IsValid => Workflow is not null;

    /// <summary>Errors only.</summary>
    public IEnumerable<ValidationDiagnostic> Errors => Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
}
