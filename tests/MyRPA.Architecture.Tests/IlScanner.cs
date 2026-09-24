using System.Reflection;
using System.Reflection.Emit;

namespace MyRPA.Architecture.Tests;

/// <summary>
/// Reads method bodies and yields the methods they call (call, callvirt, newobj, ldftn, ...).
/// Used to enforce banned APIs in compiled code, including compiler-generated async/iterator state machines.
/// </summary>
public static class IlScanner
{
    private const BindingFlags AllDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<short, OpCode> _opCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    /// <summary>All methods and constructors declared in <paramref name="type"/>.</summary>
    public static IEnumerable<MethodBase> DeclaredMethods(Type type) =>
        type.GetMethods(AllDeclared).Cast<MethodBase>().Concat(type.GetConstructors(AllDeclared));

    /// <summary>The methods referenced by <paramref name="method"/>'s IL.</summary>
    public static IEnumerable<MethodBase> ReferencedMethods(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        var typeArgs = method.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
        var methodArgs = method is MethodInfo { IsGenericMethod: true } ? method.GetGenericArguments() : null;

        var i = 0;
        while (i < il.Length)
        {
            OpCode op;
            var first = il[i++];
            if (first == 0xFE)
            {
                op = _opCodesByValue[unchecked((short)(0xFE00 | il[i++]))];
            }
            else
            {
                op = _opCodesByValue[first];
            }

            if (op.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, i);
                MethodBase? target;
                try
                {
                    target = method.Module.ResolveMethod(token, typeArgs, methodArgs);
                }
                catch (ArgumentException)
                {
                    target = null;
                }

                if (target is not null)
                {
                    yield return target;
                }
            }

            i += OperandSize(op, il, i);
        }
    }

    /// <summary>Describes why a call target is banned by ADR-0008, or <see langword="null"/> when allowed.</summary>
    public static string? BannedReason(MethodBase target)
    {
        var type = target.DeclaringType?.FullName;
        var name = target.Name;
        var parameters = target.GetParameters();
        var firstIsString = parameters.Length > 0 && parameters[0].ParameterType == typeof(string);

        return type switch
        {
            "System.Type" when name == "GetType" && firstIsString => "Type.GetType(string) resolves types from text",
            "System.Reflection.Assembly" when name is "Load" or "LoadFrom" or "LoadFile" or "UnsafeLoadFrom" or "LoadWithPartialName"
                => $"Assembly.{name} loads arbitrary code",
            "System.Reflection.Assembly" when name == "GetType" && firstIsString => "Assembly.GetType(string) resolves types from text",
            "System.AppDomain" when name.StartsWith("Load", StringComparison.Ordinal)
                || name.StartsWith("ExecuteAssembly", StringComparison.Ordinal)
                || name.StartsWith("CreateInstance", StringComparison.Ordinal)
                => $"AppDomain.{name} loads/instantiates arbitrary code",
            "System.Runtime.Loader.AssemblyLoadContext" when name.StartsWith("LoadFrom", StringComparison.Ordinal)
                => $"AssemblyLoadContext.{name} loads arbitrary code (only the plugin load context may, ADR-0014)",
            "System.Activator" when (name is "CreateInstance" or "CreateInstanceFrom") && firstIsString
                => $"Activator.{name}(string, ...) instantiates types from text",
            "System.Runtime.Serialization.Formatters.Binary.BinaryFormatter" => "BinaryFormatter is unsafe",
            // ADR-0012: no process or network execution in Phase 2 code.
            "System.Diagnostics.Process" when name == "Start" => "Process.Start executes external programs",
            "System.Net.Http.HttpClient" or "System.Net.Http.HttpMessageInvoker" or "System.Net.WebClient"
                or "System.Net.Sockets.Socket" or "System.Net.Sockets.TcpClient" or "System.Net.Sockets.TcpListener"
                or "System.Net.Sockets.UdpClient" => $"{type} performs network I/O",
            "System.Net.WebRequest" when name is "Create" or "CreateHttp" => "WebRequest performs network I/O",
            _ => null,
        };
    }

    private static int OperandSize(OpCode op, byte[] il, int operandStart) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, operandStart)),
        _ => 4,
    };
}
