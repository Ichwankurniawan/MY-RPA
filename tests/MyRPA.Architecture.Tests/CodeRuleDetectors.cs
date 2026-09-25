using System.Reflection;
using System.Runtime.CompilerServices;

namespace MyRPA.Architecture.Tests;

/// <summary>
/// Detectors for the code rules (ADR-0005, ADR-0008) on compiled types. Shared (as a linked file) with test projects
/// that inspect assemblies this project cannot reference, such as the net10.0-windows Studio (ADR-0018).
/// </summary>
public static class CodeRuleDetectors
{
    /// <summary>Async methods returning void.</summary>
    public static List<string> FindAsyncVoid(IEnumerable<Type> types) =>
    [
        .. types.SelectMany(IlScanner.DeclaredMethods)
            .OfType<MethodInfo>()
            .Where(m => m.ReturnType == typeof(void) && m.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false))
            .Select(m => $"{m.DeclaringType?.FullName}.{m.Name} is async void"),
    ];

    /// <summary>Static fields that are neither const nor readonly.</summary>
    public static List<string> FindMutableStatics(IEnumerable<Type> types) =>
    [
        .. types.Where(t => !t.Name.StartsWith('<') && !t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(f => !f.IsLiteral && !f.IsInitOnly && !f.Name.Contains('<', StringComparison.Ordinal)
                && !f.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            .Select(f => $"{f.DeclaringType?.FullName}.{f.Name} is a mutable static field"),
    ];

    /// <summary>Calls to banned APIs (see <see cref="IlScanner.BannedReason"/>).</summary>
    public static List<string> FindBannedCalls(IEnumerable<Type> types) =>
    [
        .. types.SelectMany(IlScanner.DeclaredMethods)
            .SelectMany(m => IlScanner.ReferencedMethods(m).Select(target => (Caller: m, Reason: IlScanner.BannedReason(target))))
            .Where(x => x.Reason is not null)
            .Select(x => $"{x.Caller.DeclaringType?.FullName}.{x.Caller.Name}: {x.Reason}")
            .Distinct(StringComparer.Ordinal),
    ];
}
