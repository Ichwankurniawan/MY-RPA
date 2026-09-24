using System.Diagnostics.CodeAnalysis;
using MyRPA.Workflow.Validation;

namespace MyRPA.Workflow.Execution;

/// <summary>
/// Resolves workflow references used by <c>Core.InvokeWorkflow</c> (ADR-0012). Registered by hosts; the runtime
/// resolves it from the execution's DI scope, so implementations may cache per run.
/// </summary>
public interface IWorkflowResolver
{
    /// <summary>Resolves and validates a referenced workflow.</summary>
    /// <param name="reference">Reference text from the workflow (e.g. a relative path).</param>
    /// <param name="invokingLocation">Location of the invoking workflow.</param>
    /// <param name="rootLocation">Location of the entry workflow; references must not escape its container.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<WorkflowResolution> ResolveAsync(string reference, string invokingLocation, string rootLocation, CancellationToken cancellationToken);
}

/// <summary>Result of resolving a workflow reference.</summary>
public sealed class WorkflowResolution
{
    private WorkflowResolution(WorkflowDefinition? workflow, string? location, string? error, IReadOnlyList<ValidationDiagnostic> diagnostics)
    {
        Workflow = workflow;
        Location = location;
        Error = error;
        Diagnostics = diagnostics;
    }

    /// <summary>The validated workflow, when resolution succeeded.</summary>
    public WorkflowDefinition? Workflow { get; }

    /// <summary>Resolved location (e.g. full path).</summary>
    public string? Location { get; }

    /// <summary>Why resolution failed.</summary>
    public string? Error { get; }

    /// <summary>Validation diagnostics of the resolved workflow (may contain warnings on success).</summary>
    public IReadOnlyList<ValidationDiagnostic> Diagnostics { get; }

    /// <summary>Whether a valid workflow was resolved.</summary>
    [MemberNotNullWhen(true, nameof(Workflow), nameof(Location))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool Succeeded => Workflow is not null;

    /// <summary>Creates a successful resolution.</summary>
    /// <param name="workflow">Validated workflow.</param>
    /// <param name="location">Resolved location.</param>
    /// <param name="diagnostics">Warnings, if any.</param>
    public static WorkflowResolution Success(WorkflowDefinition workflow, string location, IReadOnlyList<ValidationDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        return new(workflow, location, null, diagnostics ?? []);
    }

    /// <summary>Creates a failed resolution.</summary>
    /// <param name="error">Why it failed.</param>
    /// <param name="location">Resolved location, when known.</param>
    /// <param name="diagnostics">Validation diagnostics, when the workflow was found but is invalid.</param>
    public static WorkflowResolution Failure(string error, string? location = null, IReadOnlyList<ValidationDiagnostic>? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new(null, location, error, diagnostics ?? []);
    }
}
