using System.Reflection;

namespace MyRPA.Architecture.Tests;

/// <summary>
/// Code-level rules on compiled src assemblies: explicit composition without global state (ADR-0005) and the
/// secure-by-default baseline (ADR-0008). Each detector is also tested against a known-bad sample so that a
/// silently broken detector cannot make the rules pass.
/// </summary>
public sealed class CodeRuleTests
{
    // The desktop Studio shell (no assembly here) is checked with the same detectors in MyRPA.Studio.Tests.
    public static TheoryData<string> SourceProjectNames() => [.. ArchitectureRules.SourceProjects.Where(r => r.Assembly is not null).Select(r => r.Name)];

    private static Assembly AssemblyOf(string name) => ArchitectureRules.SourceProjects.Single(r => r.Name == name).Assembly!;

    [Theory]
    [MemberData(nameof(SourceProjectNames))]
    public void NoAsyncVoidMethods(string name) => Assert.Empty(CodeRuleDetectors.FindAsyncVoid(AssemblyOf(name).GetTypes()));

    [Theory]
    [MemberData(nameof(SourceProjectNames))]
    public void NoMutableStaticFields(string name) => Assert.Empty(CodeRuleDetectors.FindMutableStatics(AssemblyOf(name).GetTypes()));

    [Theory]
    [MemberData(nameof(SourceProjectNames))]
    public void NoBannedApiCalls(string name) =>
        Assert.DoesNotContain(CodeRuleDetectors.FindBannedCalls(AssemblyOf(name).GetTypes()), v => !ArchitectureRules.IsExempt(v));

    [Fact]
    public void BannedApiExemptions_AreUsedOnlyByTheirType()
    {
        // The exemption must cover real calls (so it is not stale) and nothing outside the exempted type.
        var plugins = CodeRuleDetectors.FindBannedCalls(AssemblyOf("MyRPA.Plugins").GetTypes());

        foreach (var exemption in ArchitectureRules.BannedApiExemptions)
        {
            Assert.Contains(plugins, v => v.StartsWith(exemption.TypeName, StringComparison.Ordinal) && v.Contains(exemption.Api, StringComparison.Ordinal));
        }

        Assert.All(plugins, v => Assert.True(ArchitectureRules.IsExempt(v), v));
    }

    [Fact]
    public void Exemptions_DoNotApplyToOtherTypes()
    {
        Assert.False(ArchitectureRules.IsExempt("MyRPA.Plugins.PluginLoader.Load: AssemblyLoadContext.LoadFromStream loads arbitrary code"));
        Assert.False(ArchitectureRules.IsExempt("MyRPA.Plugins.Loading.PluginLoadContextHelper.X: AssemblyLoadContext.LoadFromStream loads arbitrary code"));
        Assert.False(ArchitectureRules.IsExempt("MyRPA.Plugins.Loading.PluginLoadContext.Load: Process.Start executes external programs"));
        Assert.True(ArchitectureRules.IsExempt("MyRPA.Plugins.Loading.PluginLoadContext.LoadVerified: AssemblyLoadContext.LoadFromStream loads arbitrary code (only the plugin load context may, ADR-0014)"));
    }

    [Fact]
    public void Detectors_FindViolationsInKnownBadSamples()
    {
        Type[] samples = [typeof(KnownBadSamples), .. typeof(KnownBadSamples).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)];

        Assert.Contains(CodeRuleDetectors.FindAsyncVoid(samples), v => v.Contains(nameof(KnownBadSamples.AsyncVoid), StringComparison.Ordinal));
        Assert.Contains(CodeRuleDetectors.FindMutableStatics(samples), v => v.Contains(nameof(KnownBadSamples.MutableState), StringComparison.Ordinal));

        var banned = CodeRuleDetectors.FindBannedCalls(samples);
        Assert.Contains(banned, v => v.Contains("Type.GetType", StringComparison.Ordinal));
        Assert.Contains(banned, v => v.Contains("Assembly.LoadFrom", StringComparison.Ordinal));
        Assert.Contains(banned, v => v.Contains("Assembly.GetType(string)", StringComparison.Ordinal));
        Assert.Contains(banned, v => v.Contains("AssemblyLoadContext.LoadFromAssemblyPath", StringComparison.Ordinal));
        Assert.Contains(banned, v => v.Contains("Activator.CreateInstance", StringComparison.Ordinal));
        // The call inside an async method lives in a compiler-generated state machine type; it must still be found.
        Assert.Contains(banned, v => v.Contains("Assembly.LoadFile", StringComparison.Ordinal));
        Assert.Contains(banned, v => v.Contains("Process.Start", StringComparison.Ordinal));
        Assert.Contains(banned, v => v.Contains("HttpClient", StringComparison.Ordinal));
    }

#pragma warning disable CA1822, CA1823, CA1859, IDE0051, IDE0052, CS0414, CA2211 // intentionally bad code used to verify detectors
    private sealed class KnownBadSamples
    {
        public static int MutableState = 1;

        public static async void AsyncVoid() => await Task.Yield();

        public static Type? ResolveTypeFromText() => Type.GetType("System.String");

        public static Assembly LoadFromPath() => Assembly.LoadFrom("plugin.dll");

        public static Type? ResolveInsideAssembly(Assembly assembly) => assembly.GetType("Some.Type");

        public static Assembly LoadIntoContext() => System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath("plugin.dll");

        public static object? CreateFromText() => Activator.CreateInstance("asm", "type");

        public static async Task<Assembly> LoadInsideAsync()
        {
            await Task.Yield();
            return Assembly.LoadFile("plugin.dll");
        }

        public static System.Diagnostics.Process? StartProcess() => System.Diagnostics.Process.Start("calc.exe");

        public static async Task<string> Download()
        {
            using var client = new System.Net.Http.HttpClient();
            return await client.GetStringAsync(new Uri("https://example.invalid/"));
        }
    }
#pragma warning restore CA1822, CA1823, CA1859, IDE0051, IDE0052, CS0414, CA2211
}
