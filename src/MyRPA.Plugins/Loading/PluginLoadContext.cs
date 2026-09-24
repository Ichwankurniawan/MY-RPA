using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using MyRPA.Sdk.Plugins;

namespace MyRPA.Plugins.Loading;

/// <summary>
/// One collectible <see cref="AssemblyLoadContext"/> per plugin (ADR-0014). This is the only type in MyRPA allowed to
/// load assemblies (enforced by the architecture tests).
/// <list type="bullet">
/// <item><b>Shared contract assemblies</b> (<see cref="SharedAssemblies"/>) always come from the host, so plugin types
/// implement the host's <c>IPlugin</c>/<c>IActivity</c>; copies shipped in the plugin directory are ignored.</item>
/// <item><b>Plugin-private dependencies</b> are resolved from the plugin's <c>.deps.json</c>, must be inside the plugin
/// directory, and are loaded into this context, so two plugins can use different versions of the same library.</item>
/// <item><b>Verified path loading</b> (ADR-0016): each file is opened with read-only sharing, hashed and compared with
/// the hash taken when the directory was inspected, and loaded from its path while that handle is still open. On
/// Windows the handle blocks writers, so the file that is mapped is the file that was verified; the assembly keeps a
/// real <see cref="Assembly.Location"/> inside the plugin directory, which libraries such as Playwright need to find
/// their companion files.</item>
/// <item>Anything else (the framework) falls back to the default context.</item>
/// </list>
/// Isolation here is about <em>loading</em> — versions, conflicts, unloading. It is <b>not</b> a security boundary:
/// plugin code runs with the host's full permissions (ADR-0015).
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly PluginDirectory _directory;
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _entryAssemblyPath;

    public PluginLoadContext(PluginId id, PluginDirectory directory, string entryAssemblyFileName)
        : base($"MyRPA.Plugin:{id}", isCollectible: true)
    {
        _directory = directory;
        _entryAssemblyPath = System.IO.Path.Combine(directory.Path, entryAssemblyFileName);
        _resolver = new AssemblyDependencyResolver(_entryAssemblyPath);
    }

    /// <summary>Assemblies that define the host/plugin contract and are always shared with the host.</summary>
    public static FrozenSet<string> SharedAssemblies { get; } = new[]
    {
        "MyRPA.Core",
        "MyRPA.Workflow",
        "MyRPA.Sdk",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Loads the verified entry assembly.</summary>
    public Assembly LoadEntryAssembly() =>
        LoadVerified(_entryAssemblyPath) ?? throw new FileNotFoundException($"Entry assembly '{_entryAssemblyPath}' is not part of the verified plugin content.");

    /// <summary>
    /// Returns the entry type named by the manifest. The lookup is confined to the plugin's own entry assembly; the name
    /// comes from the manifest of an allow-listed plugin, never from workflow input.
    /// </summary>
    public static Type? FindEntryType(Assembly entryAssembly, string typeName) =>
        entryAssembly.GetType(typeName, throwOnError: false, ignoreCase: false);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null || SharedAssemblies.Contains(assemblyName.Name))
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadVerified(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } resolved ? System.IO.Path.GetFullPath(resolved) : null;
        if (path is null || !_directory.IsInside(path) || !File.Exists(path))
        {
            return IntPtr.Zero;
        }

        using var lease = OpenVerified(path);
        return LoadUnmanagedDllFromPath(path);
    }

    private Assembly? LoadVerified(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!_directory.IsInside(fullPath) || !File.Exists(fullPath))
        {
            // Not plugin content: let the default context decide (framework assemblies) rather than loading foreign files.
            return null;
        }

        using var lease = OpenVerified(fullPath);
        return LoadFromAssemblyPath(fullPath);
    }

    /// <summary>
    /// Opens <paramref name="fullPath"/> with read-only sharing and verifies its hash. The caller loads the file while the
    /// returned handle is open, so (on Windows) nobody can change it between verification and loading.
    /// </summary>
    private FileStream OpenVerified(string fullPath)
    {
        var lease = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var actual = PluginDirectory.Hex(SHA256.HashData(lease));
            if (!_directory.FileHashes.TryGetValue(_directory.RelativePath(fullPath), out var expected)
                || !string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new FileLoadException($"'{_directory.RelativePath(fullPath)}' is not part of the verified plugin content or changed after verification.");
            }

            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }
}
