using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MyRPA.Activities;
using MyRPA.Core.Execution;
using MyRPA.Plugins;
using MyRPA.Runtime;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Validation;
using MyRPA.Workflow.Values;

namespace MyRPA.Scripting.Tests;

/// <summary>The scripting plugin loaded through the real plugin host with the given settings.</summary>
public sealed class ScriptingHost : IAsyncDisposable
{
    private static int _counter;

    private ServiceProvider? _services;

    private ScriptingHost(PluginSet plugins)
    {
        Plugins = plugins;
    }

    public PluginSet Plugins { get; }

    public static async Task<ScriptingHost> StartAsync(IReadOnlyDictionary<string, string>? settings = null, bool expectFailure = false)
    {
        var options = new PluginHostOptions();
        var source = new PluginSource { Directory = ScriptingPaths.Plugin };
        foreach (var (name, value) in settings ?? new Dictionary<string, string>())
        {
            source.Settings[name] = value;
        }

        options.Sources.Add(source);
        var plugins = await PluginLoader.LoadAsync(options, TestContext.Current.CancellationToken);
        var host = new ScriptingHost(plugins);
        if (expectFailure)
        {
            return host;
        }

        Assert.False(plugins.HasRequiredFailures, string.Join(Environment.NewLine, plugins.Diagnostics));
        host._services = new ServiceCollection().AddLogging().AddMyRpaRuntime().AddMyRpaActivities().AddMyRpaPlugins(plugins)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await Plugins.DisposeAsync();
    }

    /// <summary>Runs one script activity; <paramref name="inputs"/> become the workflow argument "data" passed as its inputs.</summary>
    public Task<WorkflowExecutionResult> RunAsync(string type, string code, IReadOnlyDictionary<string, object?>? inputs = null, string? extra = null)
    {
        var output = type == "Code.Python" ? ", \"output\": \"printed\"" : string.Empty;
        var node = $$"""{ "id": "c{{Interlocked.Increment(ref _counter)}}", "type": "{{type}}", "properties": { "code": {{System.Text.Json.JsonSerializer.Serialize(code)}}, "inputs": "data", "result": "result"{{output}}{{(extra is null ? string.Empty : ", " + extra)}} } }""";
        var json = $$"""
            { "schemaVersion": "1.0", "id": "script-test", "name": "Script test", "version": "1.0.0",
              "arguments": [ { "name": "data", "direction": "In", "type": "Object" }, { "name": "result", "direction": "Out", "type": "Object" }, { "name": "printed", "direction": "Out", "type": "Object" } ],
              "root": { "id": "main", "type": "Core.Sequence", "children": [ {{node}} ] } }
            """;
        var load = _services!.GetRequiredService<WorkflowLoader>().Load(json);
        Assert.True(load.IsValid, string.Join(Environment.NewLine, load.Diagnostics));
        return _services!.GetRequiredService<IWorkflowRunner>().RunAsync(
            load.Workflow!,
            new WorkflowRunRequest { Arguments = new Dictionary<string, object?> { ["data"] = WorkflowValues.Dictionary(inputs ?? new Dictionary<string, object?>()) } },
            TestContext.Current.CancellationToken);
    }

    public static void AssertSucceeded(WorkflowExecutionResult result) =>
        Assert.True(result.Status == ExecutionStatus.Succeeded, $"{result.Status}: {result.Error?.ErrorType} {result.Error?.Message}");

    public static void AssertFailed(WorkflowExecutionResult result, string errorType)
    {
        Assert.True(result.Status == ExecutionStatus.Failed, $"Expected Failed/{errorType} but was {result.Status}: {result.Error?.ErrorType} {result.Error?.Message}");
        Assert.Equal(errorType, result.Error!.ErrorType);
    }
}

public static class ScriptingPaths
{
    public static string Plugin { get; } = Path.GetFullPath(
        typeof(ScriptingPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ScriptingPluginDirectory").Value!);

    /// <summary>A Python interpreter: MYRPA_TEST_PYTHON, or python3/python on PATH (not the Windows Store alias); null when none.</summary>
    public static string? Python { get; } = FindPython();

    private static string? FindPython()
    {
        if (Environment.GetEnvironmentVariable("MYRPA_TEST_PYTHON") is { Length: > 0 } configured)
        {
            return configured;
        }

        var names = OperatingSystem.IsWindows() ? new[] { "python.exe", "python3.exe" } : ["python3", "python"];
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (folder.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var name in names)
            {
                var candidate = Path.Combine(folder, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}

/// <summary>The errorType values workflows see (ADR-0045), as strings: the tests never compile against the plugin.</summary>
public static class ErrorTypes
{
    public const string ScriptSyntax = "ScriptSyntax";
    public const string ScriptError = "ScriptError";
    public const string ScriptLimit = "ScriptLimit";
    public const string Timeout = "Timeout";
    public const string InvalidResult = "InvalidResult";
    public const string PythonNotConfigured = "PythonNotConfigured";
    public const string InvalidInput = "InvalidInput";
}

/// <summary>Code.JavaScript (ADR-0045): values as JSON both ways, no way out of the sandbox, every limit enforced.</summary>
public sealed class JavaScriptTests
{
    private static IReadOnlyDictionary<string, object?> Data(params (string Key, object? Value)[] values) =>
        WorkflowValues.Dictionary(values.Select(v => new KeyValuePair<string, object?>(v.Key, v.Value)));

    [Fact]
    public async Task AScript_ReadsInputs_AndReturnsAWorkflowValue()
    {
        await using var host = await ScriptingHost.StartAsync();
        var lines = WorkflowValues.List(["INV-1;10.5", "note", "INV-2;3"]);

        var result = await host.RunAsync("Code.JavaScript", """
            const invoices = inputs.lines.filter(l => l.startsWith('INV')).map(l => {
              const [number, amount] = l.split(';');
              return { number, amount: Number(amount) };
            });
            return { invoices, total: invoices.reduce((s, i) => s + i.amount, 0), ok: true, none: null };
            """, Data(("lines", lines)));

        ScriptingHost.AssertSucceeded(result);
        var value = (IReadOnlyDictionary<string, object?>)result.Outputs["result"]!;
        var invoices = (IReadOnlyList<object?>)value["invoices"]!;
        Assert.Equal(2, invoices.Count);
        Assert.Equal("INV-1", ((IReadOnlyDictionary<string, object?>)invoices[0]!)["number"]);
        Assert.Equal(13.5m, value["total"]);
        Assert.Equal(true, value["ok"]);
        Assert.Null(value["none"]);
    }

    [Fact]
    public async Task AScriptReturningNothing_GivesNull()
    {
        await using var host = await ScriptingHost.StartAsync();

        var result = await host.RunAsync("Code.JavaScript", "const x = 1;");

        ScriptingHost.AssertSucceeded(result);
        Assert.Null(result.Outputs["result"]);
    }

    [Fact]
    public async Task TheSandbox_HasNoWayToDotNet_TheNetwork_Files_OrDynamicCode()
    {
        await using var host = await ScriptingHost.StartAsync();

        var result = await host.RunAsync("Code.JavaScript", """
            return [typeof System, typeof importNamespace, typeof require, typeof fetch, typeof process, typeof XMLHttpRequest, typeof clr, typeof host];
            """);

        ScriptingHost.AssertSucceeded(result);
        Assert.All((IReadOnlyList<object?>)result.Outputs["result"]!, t => Assert.Equal("undefined", t));
    }

    [Theory]
    [InlineData("return eval('1 + 1');")]
    [InlineData("return new Function('return 1')();")]
    public async Task StringCompilation_IsRefused(string code)
    {
        await using var host = await ScriptingHost.StartAsync();

        var result = await host.RunAsync("Code.JavaScript", code);

        ScriptingHost.AssertFailed(result, ErrorTypes.ScriptError);
    }

    [Fact]
    public async Task AnEndlessLoop_FailsWithTimeout()
    {
        await using var host = await ScriptingHost.StartAsync(new Dictionary<string, string> { ["maxStatements"] = "2000000000" });
        var clock = Stopwatch.StartNew();

        var result = await host.RunAsync("Code.JavaScript", "while (true) { }", extra: "\"timeoutMs\": \"500\"");

        ScriptingHost.AssertFailed(result, ErrorTypes.Timeout);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"stopped after {clock.Elapsed}");
    }

    [Theory]
    [InlineData("let i = 0; while (true) { i++; }", "maxStatements", "100000")]
    [InlineData("const a = []; while (true) { a.push('x'.repeat(10000)); }", "maxMemoryBytes", "16777216")]
    [InlineData("function f(n) { return f(n + 1); } return f(0);", "maxRecursion", "100")]
    public async Task EachLimit_StopsTheScript(string code, string setting, string value)
    {
        await using var host = await ScriptingHost.StartAsync(new Dictionary<string, string> { [setting] = value });

        var result = await host.RunAsync("Code.JavaScript", code);

        ScriptingHost.AssertFailed(result, ErrorTypes.ScriptLimit);
        Assert.Contains(setting, result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASyntaxError_FailsScriptSyntax_WithTheUsersLine()
    {
        await using var host = await ScriptingHost.StartAsync();

        var result = await host.RunAsync("Code.JavaScript", "const a = 1;\nreturn (a +;");

        ScriptingHost.AssertFailed(result, ErrorTypes.ScriptSyntax);
        Assert.Contains("(line 2)", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThrownError_FailsScriptError_WithItsMessageAndLine()
    {
        await using var host = await ScriptingHost.StartAsync();

        var result = await host.RunAsync("Code.JavaScript", "const total = 0;\nif (total === 0) throw new Error('no invoices today');\nreturn total;");

        ScriptingHost.AssertFailed(result, ErrorTypes.ScriptError);
        Assert.Contains("no invoices today", result.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("(line 2)", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResultThatIsNotJson_FailsScriptError()
    {
        await using var host = await ScriptingHost.StartAsync();

        var result = await host.RunAsync("Code.JavaScript", "return 10n;");

        ScriptingHost.AssertFailed(result, ErrorTypes.ScriptError);
    }

    [Fact]
    public async Task TheCode_IsLiteral_DataNeverBecomesCode()
    {
        await using var host = await ScriptingHost.StartAsync();

        // A value that looks like code stays a value.
        var result = await host.RunAsync("Code.JavaScript", "return inputs.text.length;", Data(("text", "'); while(true){} ('")));

        ScriptingHost.AssertSucceeded(result);
        Assert.Equal(20L, result.Outputs["result"]);
    }
}

/// <summary>
/// Code.Python (ADR-0045) with a real interpreter (MYRPA_TEST_PYTHON or one on PATH; skipped without one): values as JSON,
/// printed output, errors with the user's line, and a timeout that kills the process.
/// </summary>
public sealed class PythonTests
{
    private static string Python()
    {
        if (ScriptingPaths.Python is null)
        {
            Assert.Skip("No Python interpreter: install Python or set MYRPA_TEST_PYTHON.");
        }

        return ScriptingPaths.Python!;
    }

    private static IReadOnlyDictionary<string, object?> Data(params (string Key, object? Value)[] values) =>
        WorkflowValues.Dictionary(values.Select(v => new KeyValuePair<string, object?>(v.Key, v.Value)));

    [Fact]
    public async Task WithoutPythonPath_PythonIsRefused()
    {
        await using var host = await ScriptingHost.StartAsync();

        var result = await host.RunAsync("Code.Python", "return 1");

        ScriptingHost.AssertFailed(result, ErrorTypes.PythonNotConfigured);
    }

    [Fact]
    public async Task APythonPathThatDoesNotExist_IsRefusedWhenThePluginLoads()
    {
        await using var host = await ScriptingHost.StartAsync(new Dictionary<string, string> { ["pythonPath"] = Path.Combine(Path.GetTempPath(), "no-such-python.exe") }, expectFailure: true);

        Assert.True(host.Plugins.HasRequiredFailures);
    }

    [Fact]
    public async Task AScript_ReadsInputs_Prints_AndReturnsAWorkflowValue()
    {
        await using var host = await ScriptingHost.StartAsync(new Dictionary<string, string> { ["pythonPath"] = Python() });
        var lines = WorkflowValues.List(["INV-1;10.5", "note", "INV-2;3", "Zoë €"]);

        var result = await host.RunAsync("Code.Python", """
            invoices = [l.split(';') for l in inputs['lines'] if l.startswith('INV')]
            print('found', len(invoices))
            return {'numbers': [n for n, _ in invoices], 'total': sum(float(a) for _, a in invoices), 'last': inputs['lines'][-1], 'none': None}
            """, Data(("lines", lines)));

        ScriptingHost.AssertSucceeded(result);
        var value = (IReadOnlyDictionary<string, object?>)result.Outputs["result"]!;
        Assert.Equal(["INV-1", "INV-2"], ((IReadOnlyList<object?>)value["numbers"]!).Cast<string>());
        Assert.Equal(13.5m, value["total"]);
        Assert.Equal("Zoë €", value["last"]);
        Assert.Null(value["none"]);
        Assert.Contains("found 2", (string)result.Outputs["printed"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARaisedError_FailsScriptError_WithItsMessageAndLine()
    {
        await using var host = await ScriptingHost.StartAsync(new Dictionary<string, string> { ["pythonPath"] = Python() });

        var result = await host.RunAsync("Code.Python", "total = 0\nif total == 0:\n    raise ValueError('no invoices today')\nreturn total");

        ScriptingHost.AssertFailed(result, ErrorTypes.ScriptError);
        Assert.Contains("ValueError: no invoices today", result.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("(line 3)", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASyntaxError_FailsScriptSyntax()
    {
        await using var host = await ScriptingHost.StartAsync(new Dictionary<string, string> { ["pythonPath"] = Python() });

        var result = await host.RunAsync("Code.Python", "return (1 +");

        ScriptingHost.AssertFailed(result, ErrorTypes.ScriptSyntax);
    }

    [Fact]
    public async Task ALongScript_IsStopped_AfterItsTimeout()
    {
        await using var host = await ScriptingHost.StartAsync(new Dictionary<string, string> { ["pythonPath"] = Python() });
        var clock = Stopwatch.StartNew();

        var result = await host.RunAsync("Code.Python", "import time\ntime.sleep(60)\nreturn 1", extra: "\"timeoutMs\": \"1500\"");

        ScriptingHost.AssertFailed(result, ErrorTypes.Timeout);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"stopped after {clock.Elapsed}");
    }

    [Fact]
    public async Task AResultPythonCannotWriteAsJson_IsWrittenAsText()
    {
        await using var host = await ScriptingHost.StartAsync(new Dictionary<string, string> { ["pythonPath"] = Python() });

        var result = await host.RunAsync("Code.Python", "import datetime\nreturn {'when': datetime.date(2026, 10, 10)}");

        ScriptingHost.AssertSucceeded(result);
        Assert.Equal("2026-10-10", ((IReadOnlyDictionary<string, object?>)result.Outputs["result"]!)["when"]);
    }
}
