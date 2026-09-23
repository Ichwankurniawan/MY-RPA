using System.Reflection;
using System.Runtime.CompilerServices;

namespace MyRPA.Architecture.Tests;

/// <summary>
/// Code-level rules on compiled src assemblies: explicit composition without global state (ADR-0005) and the
/// secure-by-default baseline (ADR-0008). Each detector is also tested against a known-bad sample so that a
/// silently broken detector cannot make the rules pass.
/// </summary>
public sealed class CodeRuleTests
{
    public static TheoryData<string> SourceProjectNames() => [.. ArchitectureRules.SourceProjects.Select(r => r.Name)];

    private static Assembly AssemblyOf(string name) => ArchitectureRules.SourceProjects.Single(r => r.Name == name).Assembly;

    [Theory]
    [MemberData(nameof(SourceProjectNames))]
    public void NoAsyncVoidMethods(string name) => Assert.Empty(FindAsyncVoid(AssemblyOf(name).GetTypes()));

    [Theory]
    [MemberData(nameof(SourceProjectNames))]
    public void NoMutableStaticFields(string name) => Assert.Empty(FindMutableStatics(AssemblyOf(name).GetTypes()));

    [Theory]
    [MemberData(nameof(SourceProjectNames))]
    public void NoBannedApiCalls(string name) => Assert.Empty(FindBannedCalls(AssemblyOf(name).GetTypes()));

    [Fact]
    public void Detectors_FindViolationsInKnownBadSamples()
    {
        Type[] samples = [typeof(KnownBadSamples), .. typeof(KnownBadSamples).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)];

        Assert.Contains(FindAsyncVoid(samples), v => v.Contains(nameof(KnownBadSamples.AsyncVoid), StringComparison.Ordinal));
        Assert.Contains(FindMutableStatics(samples), v => v.Contains(nameof(KnownBadSamples.MutableState), StringComparison.Ordinal));

        var banned = FindBannedCalls(samples);
        Assert.Contains(banned, v => v.Contains("Type.GetType", StringComparison.Ordinal));
        Assert.Contains(banned, v => v.Contains("Assembly.LoadFrom", StringComparison.Ordinal));
        Assert.Contains(banned, v => v.Contains("Activator.CreateInstance", StringComparison.Ordinal));
        // The call inside an async method lives in a compiler-generated state machine type; it must still be found.
        Assert.Contains(banned, v => v.Contains("Assembly.LoadFile", StringComparison.Ordinal));
    }

    private static List<string> FindAsyncVoid(IEnumerable<Type> types) =>
    [
        .. types.SelectMany(IlScanner.DeclaredMethods)
            .OfType<MethodInfo>()
            .Where(m => m.ReturnType == typeof(void) && m.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false))
            .Select(m => $"{m.DeclaringType?.FullName}.{m.Name} is async void"),
    ];

    private static List<string> FindMutableStatics(IEnumerable<Type> types) =>
    [
        .. types.Where(t => !t.Name.StartsWith('<') && !t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(f => !f.IsLiteral && !f.IsInitOnly && !f.Name.Contains('<', StringComparison.Ordinal)
                && !f.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .Select(f => $"{f.DeclaringType?.FullName}.{f.Name} is a mutable static field"),
    ];

    private static List<string> FindBannedCalls(IEnumerable<Type> types) =>
    [
        .. types.SelectMany(IlScanner.DeclaredMethods)
            .SelectMany(m => IlScanner.ReferencedMethods(m).Select(target => (Caller: m, Reason: IlScanner.BannedReason(target))))
            .Where(x => x.Reason is not null)
            .Select(x => $"{x.Caller.DeclaringType?.FullName}.{x.Caller.Name}: {x.Reason}")
            .Distinct(StringComparer.Ordinal),
    ];

#pragma warning disable CA1822, CA1823, CA1859, IDE0051, IDE0052, CS0414, CA2211 // intentionally bad code used to verify detectors
    private sealed class KnownBadSamples
    {
        public static int MutableState = 1;

        public static async void AsyncVoid() => await Task.Yield();

        public static Type? ResolveTypeFromText() => Type.GetType("System.String");

        public static Assembly LoadFromPath() => Assembly.LoadFrom("plugin.dll");

        public static object? CreateFromText() => Activator.CreateInstance("asm", "type");

        public static async Task<Assembly> LoadInsideAsync()
        {
            await Task.Yield();
            return Assembly.LoadFile("plugin.dll");
        }
    }
#pragma warning restore CA1822, CA1823, CA1859, IDE0051, IDE0052, CS0414, CA2211
}
