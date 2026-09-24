using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyRPA.Core.Activities;
using MyRPA.Sdk;
using MyRPA.Sdk.Automation;
using MyRPA.Sdk.Plugins;
using MyRPA.Workflow.Validation;

namespace MyRPA.Plugins.Manifest;

/// <summary>The outcome of reading a manifest.</summary>
/// <param name="Manifest">The manifest, or <see langword="null"/> when there are errors.</param>
/// <param name="Diagnostics">Every problem found (errors and warnings).</param>
public sealed record PluginManifestReadResult(PluginManifest? Manifest, IReadOnlyList<PluginDiagnostic> Diagnostics);

/// <summary>
/// Reads <c>myrpa-plugin.json</c> (ADR-0014). Checks structure and formats and reports <em>every</em> problem; never
/// throws for bad input and never loads code. Compatibility with this host (SDK, framework, dependencies, trust) is
/// decided later by <see cref="PluginLoader"/>. Stateless and thread-safe.
/// </summary>
public static partial class PluginManifestReader
{
    /// <summary>Maximum manifest size in bytes.</summary>
    public const int MaxManifestBytes = 1024 * 1024;

    private static readonly JsonDocumentOptions _options = new() { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly HashSet<string> _knownFields = new(StringComparer.Ordinal)
    {
        "manifestVersion", "id", "name", "version", "description", "sdkVersion", "targetFramework", "entryPoint",
        "capabilities", "activities", "providers", "dependencies", "$schema",
    };

    /// <summary>Reads and validates manifest JSON.</summary>
    /// <param name="json">Manifest text.</param>
    /// <param name="source">The plugin directory (used in diagnostics).</param>
    public static PluginManifestReadResult Read(string json, string source)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(source);
        var reader = new Reader(source);
        return new PluginManifestReadResult(reader.Read(json), reader.Diagnostics);
    }

    /// <summary>Returns <see langword="true"/> when <paramref name="moniker"/> has a supported target framework format.</summary>
    /// <param name="moniker">For example <c>net10.0</c>, <c>net10.0-windows</c> or <c>netstandard2.0</c>.</param>
    public static bool IsTargetFrameworkFormat(string? moniker) => moniker is not null && TargetFrameworkPattern().IsMatch(moniker);

    [GeneratedRegex(@"^(net(?<major>[1-9][0-9]?)\.(?<minor>[0-9])(?<platform>-windows([0-9]+(\.[0-9]+){0,3})?)?|netstandard2\.[01])$", RegexOptions.CultureInvariant)]
    internal static partial Regex TargetFrameworkPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]*\.dll$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AssemblyFilePattern();

    private sealed class Reader(string source)
    {
        private string? _pluginId;

        public List<PluginDiagnostic> Diagnostics { get; } = [];

        private bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

        public PluginManifest? Read(string json)
        {
            if (json.Length > MaxManifestBytes)
            {
                Error(PluginDiagnosticCodes.ManifestMalformed, "$", $"The manifest is larger than {MaxManifestBytes / 1024} KB.");
                return null;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json, _options);
            }
            catch (JsonException ex)
            {
                var at = ex.LineNumber is { } line
                    ? string.Create(CultureInfo.InvariantCulture, $" (line {line + 1}, position {ex.BytePositionInLine + 1})")
                    : string.Empty;
                Error(PluginDiagnosticCodes.ManifestMalformed, "$", $"The manifest is not valid JSON{at}.");
                return null;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    Error(PluginDiagnosticCodes.ManifestMalformed, "$", "The manifest must be a JSON object.");
                    return null;
                }

                // The format version decides how the rest is read, so it is checked first (as for workflows, ADR-0011).
                var manifestVersion = ReadVersion(root);
                if (manifestVersion is null)
                {
                    return null;
                }

                var idText = RequiredString(root, "id");
                PluginId? id = null;
                if (idText is not null && !PluginId.TryCreate(idText, out id))
                {
                    Error(PluginDiagnosticCodes.InvalidField, "$.id", $"'{idText}' is not a valid plugin id (dot-separated ASCII letters/digits, such as 'Contoso.Browser').");
                }

                _pluginId = id?.Value;

                foreach (var property in root.EnumerateObject().Where(p => !_knownFields.Contains(p.Name)))
                {
                    Warning(PluginDiagnosticCodes.UnknownField, "$." + property.Name, $"Unknown field '{property.Name}' is ignored.");
                }

                var name = RequiredString(root, "name");
                var version = ParseRequired<PluginVersion>(root, "version", "a plugin version (MAJOR.MINOR.PATCH[-prerelease])", PluginVersion.TryParse);
                var description = OptionalString(root, "description");
                var sdkVersion = ParseRequired<SdkVersion>(root, "sdkVersion", "an SDK version ('major.minor')", SdkVersion.TryParse);
                var framework = RequiredString(root, "targetFramework");
                if (framework is not null && !IsTargetFrameworkFormat(framework))
                {
                    Error(PluginDiagnosticCodes.InvalidField, "$.targetFramework", $"'{framework}' is not a supported target framework (netX.Y, netX.Y-windows or netstandard2.0/2.1).");
                }

                var entryPoint = ReadEntryPoint(root);
                var capabilities = ReadNames(root, "capabilities", "a capability", ReadCapability);
                var activities = ReadNames(root, "activities", "an activity type name", ReadActivity);
                var providers = ReadNames(root, "providers", "a provider id", ReadProvider);
                var dependencies = ReadDependencies(root, id);

                if (HasErrors)
                {
                    return null;
                }

                return new PluginManifest(
                    manifestVersion, id!, name!, version!, description, sdkVersion!, framework!, entryPoint!,
                    capabilities, activities, providers, dependencies);
            }
        }

        private SdkVersion? ReadVersion(JsonElement root)
        {
            if (!root.TryGetProperty("manifestVersion", out var element))
            {
                Error(PluginDiagnosticCodes.MissingField, "$.manifestVersion", "Required field 'manifestVersion' is missing.");
                return null;
            }

            if (element.ValueKind != JsonValueKind.String || !SdkVersion.TryParse(element.GetString(), out var version))
            {
                Error(PluginDiagnosticCodes.InvalidField, "$.manifestVersion", $"{element.GetRawText()} is not a manifest version; expected a string such as \"{PluginManifest.CurrentManifestVersion}\".");
                return null;
            }

            if (!PluginManifest.CurrentManifestVersion.Supports(version))
            {
                Error(PluginDiagnosticCodes.UnsupportedManifestVersion, "$.manifestVersion", $"Manifest version {version} is not supported; this host reads {PluginManifest.CurrentManifestVersion.Major}.0 to {PluginManifest.CurrentManifestVersion}.");
                return null;
            }

            return version;
        }

        private PluginEntryPoint? ReadEntryPoint(JsonElement root)
        {
            if (!root.TryGetProperty("entryPoint", out var element))
            {
                Error(PluginDiagnosticCodes.MissingField, "$.entryPoint", "Required field 'entryPoint' is missing.");
                return null;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                Error(PluginDiagnosticCodes.InvalidField, "$.entryPoint", "'entryPoint' must be an object with 'assembly' and 'type'.");
                return null;
            }

            var assembly = RequiredString(element, "assembly", "$.entryPoint");
            if (assembly is not null && !AssemblyFilePattern().IsMatch(assembly))
            {
                Error(PluginDiagnosticCodes.InvalidField, "$.entryPoint.assembly", $"'{assembly}' must be the file name of a .dll in the plugin directory (no path).");
            }

            var type = RequiredString(element, "type", "$.entryPoint");
            if (type is not null && (type.Length > 512 || !TypeNamePattern().IsMatch(type)))
            {
                Error(PluginDiagnosticCodes.InvalidField, "$.entryPoint.type", $"'{type}' must be the full name of a non-nested, non-generic type.");
            }

            foreach (var property in element.EnumerateObject().Where(p => p.Name is not ("assembly" or "type")))
            {
                Warning(PluginDiagnosticCodes.UnknownField, "$.entryPoint." + property.Name, $"Unknown field '{property.Name}' is ignored.");
            }

            return assembly is null || type is null ? null : new PluginEntryPoint(assembly, type);
        }

        private string? ReadCapability(string value, string path)
        {
            if (!PluginCapabilities.Known.Contains(value))
            {
                Warning(PluginDiagnosticCodes.UnknownField, path, $"Unknown capability '{value}' (known: {string.Join(", ", PluginCapabilities.Known.Order(StringComparer.Ordinal))}).");
            }

            return value;
        }

        private ActivityTypeName? ReadActivity(string value, string path)
        {
            if (!ActivityTypeName.TryCreate(value, out var name))
            {
                Error(PluginDiagnosticCodes.InvalidField, path, $"'{value}' is not a valid activity type name ('Namespace.Name').");
                return null;
            }

            if (string.Equals(name.Namespace, AutomationSdk.ReservedNamespace, StringComparison.OrdinalIgnoreCase))
            {
                Error(PluginDiagnosticCodes.NameConflict, path, $"The '{AutomationSdk.ReservedNamespace}' namespace is reserved for built-in activities.");
                return null;
            }

            return name;
        }

        private AutomationProviderId? ReadProvider(string value, string path)
        {
            if (!AutomationProviderId.TryCreate(value, out var id))
            {
                Error(PluginDiagnosticCodes.InvalidField, path, $"'{value}' is not a valid provider id ('Namespace.Name').");
                return null;
            }

            if (string.Equals(id.Namespace, AutomationSdk.ReservedNamespace, StringComparison.OrdinalIgnoreCase))
            {
                Error(PluginDiagnosticCodes.NameConflict, path, $"The '{AutomationSdk.ReservedNamespace}' namespace is reserved.");
                return null;
            }

            return id;
        }

        private List<T> ReadNames<T>(JsonElement root, string field, string what, Func<string, string, T?> convert)
            where T : class
        {
            var result = new List<T>();
            if (!root.TryGetProperty(field, out var array))
            {
                return result;
            }

            if (array.ValueKind != JsonValueKind.Array)
            {
                Error(PluginDiagnosticCodes.InvalidField, "$." + field, $"'{field}' must be an array of strings.");
                return result;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var item in array.EnumerateArray())
            {
                var path = string.Create(CultureInfo.InvariantCulture, $"$.{field}[{index++}]");
                if (item.ValueKind != JsonValueKind.String)
                {
                    Error(PluginDiagnosticCodes.InvalidField, path, $"Expected {what} (a string).");
                    continue;
                }

                var text = item.GetString()!;
                if (!seen.Add(text))
                {
                    Error(PluginDiagnosticCodes.InvalidField, path, $"'{text}' is listed more than once.");
                    continue;
                }

                if (convert(text, path) is { } value)
                {
                    result.Add(value);
                }
            }

            return result;
        }

        private List<PluginDependency> ReadDependencies(JsonElement root, PluginId? self)
        {
            var result = new List<PluginDependency>();
            if (!root.TryGetProperty("dependencies", out var array))
            {
                return result;
            }

            if (array.ValueKind != JsonValueKind.Array)
            {
                Error(PluginDiagnosticCodes.InvalidField, "$.dependencies", "'dependencies' must be an array of { \"id\", \"version\" } objects.");
                return result;
            }

            var index = 0;
            foreach (var item in array.EnumerateArray())
            {
                var path = string.Create(CultureInfo.InvariantCulture, $"$.dependencies[{index++}]");
                if (item.ValueKind != JsonValueKind.Object)
                {
                    Error(PluginDiagnosticCodes.InvalidField, path, "Expected an object with 'id' and 'version'.");
                    continue;
                }

                var idText = RequiredString(item, "id", path);
                PluginId? id = null;
                if (idText is not null && !PluginId.TryCreate(idText, out id))
                {
                    Error(PluginDiagnosticCodes.InvalidField, path + ".id", $"'{idText}' is not a valid plugin id.");
                }

                var version = ParseRequired<PluginVersion>(item, "version", "a minimum plugin version (MAJOR.MINOR.PATCH)", PluginVersion.TryParse, path);
                if (id is null || version is null)
                {
                    continue;
                }

                if (id.Equals(self))
                {
                    Error(PluginDiagnosticCodes.InvalidField, path + ".id", "A plugin cannot depend on itself.");
                }
                else if (result.Any(d => d.Id.Equals(id)))
                {
                    Error(PluginDiagnosticCodes.InvalidField, path + ".id", $"Dependency '{id}' is listed more than once.");
                }
                else
                {
                    result.Add(new PluginDependency(id, version));
                }
            }

            return result;
        }

        private delegate bool TryParser<T>(string? text, out T? value);

        private T? ParseRequired<T>(JsonElement parent, string field, string expected, TryParser<T> parse, string parentPath = "$")
            where T : class
        {
            var text = RequiredString(parent, field, parentPath);
            if (text is null)
            {
                return null;
            }

            if (!parse(text, out var value))
            {
                Error(PluginDiagnosticCodes.InvalidField, $"{parentPath}.{field}", $"'{text}' is not {expected}.");
            }

            return value;
        }

        private string? RequiredString(JsonElement parent, string field, string parentPath = "$")
        {
            var path = $"{parentPath}.{field}";
            if (!parent.TryGetProperty(field, out var element) || element.ValueKind == JsonValueKind.Null)
            {
                Error(PluginDiagnosticCodes.MissingField, path, $"Required field '{field}' is missing.");
                return null;
            }

            if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            {
                Error(PluginDiagnosticCodes.InvalidField, path, $"'{field}' must be a non-empty string.");
                return null;
            }

            return element.GetString();
        }

        private string? OptionalString(JsonElement parent, string field)
        {
            if (!parent.TryGetProperty(field, out var element) || element.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (element.ValueKind != JsonValueKind.String)
            {
                Error(PluginDiagnosticCodes.InvalidField, "$." + field, $"'{field}' must be a string.");
                return null;
            }

            return element.GetString();
        }

        private void Error(string code, string path, string message) =>
            Diagnostics.Add(new PluginDiagnostic(code, DiagnosticSeverity.Error, message, source, _pluginId, path));

        private void Warning(string code, string path, string message) =>
            Diagnostics.Add(new PluginDiagnostic(code, DiagnosticSeverity.Warning, message, source, _pluginId, path));
    }
}
