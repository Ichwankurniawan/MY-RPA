using System.Globalization;
using System.Text.Json;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Serialization;

namespace MyRPA.Workflow.Validation;

/// <summary>
/// Loads workflow JSON through the full pipeline (ADR-0011): parse → schema version check → structural read →
/// semantic validation → <see cref="WorkflowDefinition"/>. Never throws for invalid input; every problem is reported
/// as a <see cref="ValidationDiagnostic"/>. Stateless and thread-safe.
/// </summary>
/// <param name="catalog">Registered activity types.</param>
public sealed class WorkflowLoader(IActivityCatalog catalog)
{
    /// <summary>Maximum JSON nesting depth accepted.</summary>
    public const int MaxDocumentDepth = 128;

    private static readonly JsonDocumentOptions _documentOptions = new()
    {
        MaxDepth = MaxDocumentDepth,
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IActivityCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <summary>Returns <see langword="true"/> when this build can read <paramref name="version"/>.</summary>
    /// <param name="version">Schema version declared by a workflow file.</param>
    public static bool IsSupported(WorkflowSchemaVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Major == WorkflowSchemaVersion.Current.Major && version.Minor <= WorkflowSchemaVersion.Current.Minor;
    }

    /// <summary>Loads and validates workflow JSON text.</summary>
    /// <param name="json">Workflow JSON.</param>
    public WorkflowLoadResult Load(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var diagnostics = new List<ValidationDiagnostic>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, _documentOptions);
        }
        catch (JsonException ex)
        {
            var location = ex.LineNumber is { } line
                ? string.Create(CultureInfo.InvariantCulture, $" (line {line + 1}, position {ex.BytePositionInLine + 1})")
                : string.Empty;
            diagnostics.Add(new ValidationDiagnostic(
                DiagnosticCodes.MalformedJson,
                DiagnosticSeverity.Error,
                $"The file is not valid JSON{location}.",
                string.IsNullOrEmpty(ex.Path) ? "$" : ex.Path));
            return new WorkflowLoadResult(null, diagnostics);
        }

        using (document)
        {
            // The schema version decides how the rest of the document is interpreted, so it is checked first; a file
            // in another format version is not reported against this version's structure.
            var version = ReadSchemaVersion(document.RootElement, diagnostics);
            if (version is null)
            {
                return new WorkflowLoadResult(null, diagnostics);
            }

            var raw = new WorkflowStructureReader(diagnostics).Read(document.RootElement)!;
            var workflow = new WorkflowSemanticValidator(_catalog, diagnostics).Validate(raw, version);
            return new WorkflowLoadResult(workflow, diagnostics);
        }
    }

    private static WorkflowSchemaVersion? ReadSchemaVersion(JsonElement root, List<ValidationDiagnostic> diagnostics)
    {
        void Error(string code, string message, string path = "$.schemaVersion") =>
            diagnostics.Add(new ValidationDiagnostic(code, DiagnosticSeverity.Error, message, path));

        if (root.ValueKind != JsonValueKind.Object)
        {
            Error(DiagnosticCodes.RootNotObject, "The workflow must be a JSON object.", "$");
            return null;
        }

        if (!root.TryGetProperty("schemaVersion", out var element))
        {
            Error(DiagnosticCodes.MissingField, "Required field 'schemaVersion' is missing.");
            return null;
        }

        if (element.ValueKind != JsonValueKind.String || !WorkflowSchemaVersion.TryParse(element.GetString(), out var version))
        {
            Error(
                DiagnosticCodes.InvalidSchemaVersion,
                $"{element.GetRawText()} is not a schema version; expected a string 'major.minor' such as \"{WorkflowSchemaVersion.Current}\".");
            return null;
        }

        if (!IsSupported(version))
        {
            Error(
                DiagnosticCodes.UnsupportedSchemaVersion,
                $"Schema version {version} is not supported; this MyRPA reads {WorkflowSchemaVersion.Current.Major}.0 to {WorkflowSchemaVersion.Current}.");
            return null;
        }

        return version;
    }
}
