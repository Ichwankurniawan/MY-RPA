using System.Reflection;
using MyRPA.Plugins.Loading;
using MyRPA.Plugins.Manifest;
using MyRPA.Sdk;
using MyRPA.Sdk.Plugins;
using MyRPA.Workflow.Validation;

namespace MyRPA.Plugins;

/// <summary>
/// Runs the plugin lifecycle for the configured sources (ADR-0014):
/// <c>Discover → Validate manifest → (trust and compatibility checks) → Load → Initialize → Register</c>.
/// The result is a <see cref="PluginSet"/> whose registrations are applied to the host with
/// <see cref="PluginSet.AddTo"/>; <c>Use</c> and <c>Dispose</c> follow the host's lifetime.
/// A plugin that fails any step is rejected with diagnostics and unloaded, and nothing it registered is applied, so a
/// broken plugin cannot corrupt the host.
/// </summary>
public static class PluginLoader
{
    /// <summary>Discovers, validates and loads the configured plugins.</summary>
    /// <param name="options">The allow-listed plugin sources and trust policy.</param>
    /// <param name="cancellationToken">Cancellation (checked between plugins).</param>
    public static async Task<PluginSet> LoadAsync(PluginHostOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var diagnostics = new List<PluginDiagnostic>();
        var failedRequired = false;

        void Reject(PluginSource source, string code, string message, string? pluginId = null)
        {
            diagnostics.Add(new PluginDiagnostic(code, DiagnosticSeverity.Error, message, source.Directory, pluginId));
            failedRequired |= source.Required;
        }

        // Discover and validate: manifests, compatibility and trust. No plugin code runs in this step.
        var candidates = new List<Candidate>();
        foreach (var source in options.Sources)
        {
            if (Discover(source, options, diagnostics) is { } candidate)
            {
                candidates.Add(candidate);
            }
            else
            {
                failedRequired |= source.Required;
            }
        }

        candidates = RejectConflicts(candidates, Reject);
        var rejectedIds = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error && d.PluginId is not null)
            .Select(d => d.PluginId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ordered = OrderByDependencies(candidates, rejectedIds, Reject);

        // Load, initialize and register, dependencies first.
        var loaded = new List<LoadedPlugin>();
        foreach (var candidate in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failedDependency = candidate.Manifest.Dependencies.FirstOrDefault(d => !loaded.Any(p => p.Manifest.Id.Equals(d.Id)));
            if (failedDependency is not null)
            {
                Reject(candidate.Source, PluginDiagnosticCodes.DependencyFailed, $"Dependency '{failedDependency.Id}' failed to load.", candidate.Manifest.Id.Value);
                continue;
            }

            if (await LoadOneAsync(candidate, Reject).ConfigureAwait(false) is { } plugin)
            {
                loaded.Add(plugin);
            }
        }

        return new PluginSet(loaded, diagnostics, failedRequired);
    }

    private static Candidate? Discover(PluginSource source, PluginHostOptions options, List<PluginDiagnostic> diagnostics)
    {
        void Error(string code, string message, string? pluginId = null) =>
            diagnostics.Add(new PluginDiagnostic(code, DiagnosticSeverity.Error, message, source.Directory, pluginId));

        if (string.IsNullOrWhiteSpace(source.Directory) || !Path.IsPathFullyQualified(source.Directory))
        {
            Error(PluginDiagnosticCodes.DirectoryInvalid, $"'{source.Directory}' must be a fully qualified directory path.");
            return null;
        }

        // A directory is a plugin only if it has a manifest; check that before reading anything else in it.
        if (Directory.Exists(source.Directory) && !File.Exists(Path.Combine(source.Directory, PluginManifest.FileName)))
        {
            Error(PluginDiagnosticCodes.ManifestMissing, $"The directory has no {PluginManifest.FileName}; only directories with a manifest are plugins.");
            return null;
        }

        var directory = PluginDirectory.Inspect(source.Directory, out var problem);
        if (directory is null)
        {
            Error(PluginDiagnosticCodes.DirectoryInvalid, problem!);
            return null;
        }

        var manifestPath = Path.Combine(directory.Path, PluginManifest.FileName);
        if (!directory.FileHashes.ContainsKey(PluginManifest.FileName))
        {
            Error(PluginDiagnosticCodes.ManifestMissing, $"{PluginManifest.FileName} must be a regular file in the plugin directory.");
            return null;
        }

        if (new FileInfo(manifestPath).Length > PluginManifestReader.MaxManifestBytes)
        {
            Error(PluginDiagnosticCodes.ManifestMalformed, $"{PluginManifest.FileName} is larger than {PluginManifestReader.MaxManifestBytes / 1024} KB.");
            return null;
        }

        var read = PluginManifestReader.Read(File.ReadAllText(manifestPath), source.Directory);
        diagnostics.AddRange(read.Diagnostics);
        if (read.Manifest is not { } manifest)
        {
            return null;
        }

        var id = manifest.Id.Value;
        var errors = diagnostics.Count;

        if (!AutomationSdk.Version.Supports(manifest.SdkVersion))
        {
            Error(PluginDiagnosticCodes.IncompatibleSdk, $"The plugin requires SDK {manifest.SdkVersion}; this host implements SDK {AutomationSdk.Version}.", id);
        }

        if (FrameworkProblem(manifest.TargetFramework) is { } frameworkProblem)
        {
            Error(PluginDiagnosticCodes.IncompatibleFramework, frameworkProblem, id);
        }

        foreach (var capability in manifest.Capabilities.Where(options.DeniedCapabilities.Contains))
        {
            Error(PluginDiagnosticCodes.CapabilityDenied, $"The plugin declares capability '{capability}', which this host denies.", id);
        }

        if (!directory.FileHashes.ContainsKey(manifest.EntryPoint.Assembly))
        {
            Error(PluginDiagnosticCodes.EntryPointInvalid, $"Entry assembly '{manifest.EntryPoint.Assembly}' is not in the plugin directory.", id);
        }

        if (source.Sha256 is { } pinned)
        {
            if (!string.Equals(pinned.Trim(), directory.Digest, StringComparison.OrdinalIgnoreCase))
            {
                Error(PluginDiagnosticCodes.IntegrityMismatch, $"The directory digest is {directory.Digest}, not the pinned {pinned.Trim()}. The plugin changed or the pin is wrong.", id);
            }
        }
        else if (options.RequireIntegrity)
        {
            Error(PluginDiagnosticCodes.IntegrityPinRequired, $"This host requires a pinned SHA-256 for every plugin. Current digest: {directory.Digest}.", id);
        }

        return diagnostics.Count == errors ? new Candidate(source, manifest, directory) : null;
    }

    private static string? FrameworkProblem(string moniker)
    {
        var match = PluginManifestReader.TargetFrameworkPattern().Match(moniker);
        if (!match.Groups["major"].Success)
        {
            return null; // netstandard2.0/2.1 run on every supported host.
        }

        var major = int.Parse(match.Groups["major"].ValueSpan, provider: System.Globalization.CultureInfo.InvariantCulture);
        if (major > Environment.Version.Major)
        {
            return $"The plugin targets {moniker}; this host runs .NET {Environment.Version.Major}.";
        }

        if (match.Groups["platform"].Success && !OperatingSystem.IsWindows())
        {
            return $"The plugin targets {moniker}, which requires Windows.";
        }

        return null;
    }

    private static List<Candidate> RejectConflicts(List<Candidate> candidates, Action<PluginSource, string, string, string?> reject)
    {
        var accepted = new List<Candidate>();
        foreach (var candidate in candidates)
        {
            var id = candidate.Manifest.Id.Value;
            if (accepted.FirstOrDefault(a => a.Manifest.Id.Equals(candidate.Manifest.Id)) is { } first)
            {
                reject(candidate.Source, PluginDiagnosticCodes.DuplicatePlugin, $"Plugin id '{id}' is already provided by '{first.Source.Directory}'.", id);
                continue;
            }

            var activity = candidate.Manifest.Activities.FirstOrDefault(n => accepted.Any(a => a.Manifest.Activities.Contains(n)));
            var provider = candidate.Manifest.Providers.FirstOrDefault(n => accepted.Any(a => a.Manifest.Providers.Contains(n)));
            if (activity is not null || provider is not null)
            {
                reject(candidate.Source, PluginDiagnosticCodes.NameConflict, $"'{(object?)activity ?? provider}' is already declared by another plugin.", id);
                continue;
            }

            accepted.Add(candidate);
        }

        return accepted;
    }

    private static List<Candidate> OrderByDependencies(
        List<Candidate> candidates, HashSet<string> rejectedIds, Action<PluginSource, string, string, string?> reject)
    {
        // Reject plugins whose dependencies are missing or incompatible, repeatedly, until the set is closed.
        var remaining = candidates.ToList();
        bool changed;
        do
        {
            changed = false;
            foreach (var candidate in remaining.ToList())
            {
                foreach (var dependency in candidate.Manifest.Dependencies)
                {
                    var target = remaining.FirstOrDefault(c => c.Manifest.Id.Equals(dependency.Id));
                    var id = candidate.Manifest.Id.Value;
                    string? problem = null;
                    string code = PluginDiagnosticCodes.DependencyMissing;
                    if (target is null)
                    {
                        var wasConfigured = candidates.Any(c => c.Manifest.Id.Equals(dependency.Id)) || rejectedIds.Contains(dependency.Id.Value);
                        code = wasConfigured ? PluginDiagnosticCodes.DependencyFailed : PluginDiagnosticCodes.DependencyMissing;
                        problem = wasConfigured
                            ? $"Dependency '{dependency.Id}' was rejected."
                            : $"Dependency '{dependency.Id}' ({dependency.MinimumVersion} or later, same major) is not configured.";
                    }
                    else if (!target.Manifest.Version.IsCompatibleWith(dependency.MinimumVersion))
                    {
                        code = PluginDiagnosticCodes.DependencyVersion;
                        problem = $"Dependency '{dependency.Id}' is version {target.Manifest.Version}; {dependency.MinimumVersion} or later with the same major version is required.";
                    }

                    if (problem is not null)
                    {
                        reject(candidate.Source, code, problem, id);
                        remaining.Remove(candidate);
                        changed = true;
                        break;
                    }
                }
            }
        }
        while (changed);

        // Topological order (dependencies first); whatever cannot be ordered is in a cycle.
        var ordered = new List<Candidate>();
        while (remaining.Count > 0)
        {
            var ready = remaining.FirstOrDefault(c => c.Manifest.Dependencies.All(d => ordered.Any(o => o.Manifest.Id.Equals(d.Id))));
            if (ready is null)
            {
                foreach (var candidate in remaining)
                {
                    reject(candidate.Source, PluginDiagnosticCodes.DependencyCycle,
                        $"Dependency cycle among: {string.Join(", ", remaining.Select(c => c.Manifest.Id))}.", candidate.Manifest.Id.Value);
                }

                break;
            }

            ordered.Add(ready);
            remaining.Remove(ready);
        }

        return ordered;
    }

    private static async Task<LoadedPlugin?> LoadOneAsync(Candidate candidate, Action<PluginSource, string, string, string?> reject)
    {
        var manifest = candidate.Manifest;
        var id = manifest.Id.Value;
        var info = new PluginInfo(manifest.Id, manifest.Version, candidate.Directory.Path);
        var context = new PluginLoadContext(manifest.Id, candidate.Directory, manifest.EntryPoint.Assembly);
        IPlugin? plugin = null;

        async Task<LoadedPlugin?> Fail(string code, string message)
        {
            reject(candidate.Source, code, message, id);
            await DisposeQuietlyAsync(plugin).ConfigureAwait(false);
            context.Unload();
            return null;
        }

#pragma warning disable CA1031 // Plugin code may throw anything; every failure must become a diagnostic, not a host crash.
        // Load
        try
        {
            var assembly = context.LoadEntryAssembly();
            var type = PluginLoadContext.FindEntryType(assembly, manifest.EntryPoint.Type);
            if (EntryTypeProblem(type, manifest.EntryPoint.Type) is { } problem)
            {
                return await Fail(PluginDiagnosticCodes.EntryPointInvalid, problem).ConfigureAwait(false);
            }

            plugin = (IPlugin)Activator.CreateInstance(type!)!;
        }
        catch (Exception ex)
        {
            return await Fail(PluginDiagnosticCodes.LoadFailed, $"Loading failed: {Describe(ex)}").ConfigureAwait(false);
        }

        // Initialize
        try
        {
            plugin.Initialize(new PluginContext(info, AutomationSdk.Version, new Dictionary<string, string>(candidate.Source.Settings, StringComparer.Ordinal)));
        }
        catch (Exception ex)
        {
            return await Fail(PluginDiagnosticCodes.InitializationFailed, $"Initialize failed: {Describe(ex)}").ConfigureAwait(false);
        }

        // Register (staged, then validated as a whole)
        var registrar = new PluginRegistrar(info, manifest, context);
        try
        {
            plugin.Register(registrar);
        }
        catch (Exception ex)
        {
            return await Fail(PluginDiagnosticCodes.RegistrationFailed, $"Register failed: {Describe(ex)}").ConfigureAwait(false);
        }
#pragma warning restore CA1031

        var problems = registrar.Validate();
        if (problems.Count > 0)
        {
            return await Fail(PluginDiagnosticCodes.RegistrationFailed, string.Join(" ", problems)).ConfigureAwait(false);
        }

        return new LoadedPlugin(manifest, candidate.Directory.Path, candidate.Directory.Digest, context, plugin, registrar);
    }

    private static string? EntryTypeProblem(Type? type, string name)
    {
        if (type is null)
        {
            return $"Type '{name}' was not found in the entry assembly.";
        }

        if (!type.IsPublic || !type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
        {
            return $"Type '{name}' must be a public, non-abstract, non-generic class.";
        }

        if (!typeof(IPlugin).IsAssignableFrom(type))
        {
            return $"Type '{name}' does not implement {typeof(IPlugin).FullName} (from the host's MyRPA.Sdk).";
        }

        return type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is null
            ? $"Type '{name}' needs a public parameterless constructor."
            : null;
    }

    /// <summary>Disposes a plugin entry object; returns the failure instead of throwing.</summary>
    internal static async Task<Exception?> DisposeQuietlyAsync(object? plugin)
    {
#pragma warning disable CA1031 // Plugin cleanup must never crash the host; failures are reported by the caller.
        try
        {
            switch (plugin)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
#pragma warning restore CA1031
    }

    private static string Describe(Exception ex) =>
        ex is TargetInvocationException { InnerException: { } inner } ? Describe(inner) : $"{ex.GetType().Name}: {ex.Message}";

    private sealed record Candidate(PluginSource Source, PluginManifest Manifest, PluginDirectory Directory);
}
