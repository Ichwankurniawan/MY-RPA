using System.Globalization;

namespace MyRPA.Workflow.Validation;

/// <summary>Severity of a validation diagnostic.</summary>
public enum DiagnosticSeverity
{
    /// <summary>The workflow cannot be executed.</summary>
    Error = 0,

    /// <summary>Suspicious but executable (e.g. unknown fields kept for forward compatibility).</summary>
    Warning = 1,
}

/// <summary>One structured validation finding (ADR-0011).</summary>
/// <param name="Code">Stable code, see <see cref="DiagnosticCodes"/>.</param>
/// <param name="Severity">Severity.</param>
/// <param name="Message">Human-readable message.</param>
/// <param name="Path">JSON path of the offending element, e.g. <c>$.root.children[1].id</c>.</param>
/// <param name="NodeId">Node id when the finding belongs to a node with a readable id.</param>
public sealed record ValidationDiagnostic(string Code, DiagnosticSeverity Severity, string Message, string Path, string? NodeId = null)
{
    /// <summary>Formats the diagnostic like a compiler message: <c>error MYRPA1031 $.root...: message</c>.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{(Severity == DiagnosticSeverity.Error ? "error" : "warning")} {Code} {Path}: {Message}");
}
