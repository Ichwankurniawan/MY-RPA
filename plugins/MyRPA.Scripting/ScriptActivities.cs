using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Jint.Runtime;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;

namespace MyRPA.Scripting;

/// <summary>The errorType values of the scripting activities (ADR-0045).</summary>
public static class ScriptErrorTypes
{
    /// <summary>The code does not parse.</summary>
    public const string ScriptSyntax = "ScriptSyntax";

    /// <summary>The script threw an error (the message carries it and its line).</summary>
    public const string ScriptError = "ScriptError";

    /// <summary>The script went past the statement, memory or recursion limit.</summary>
    public const string ScriptLimit = "ScriptLimit";

    /// <summary>The script ran longer than its timeout.</summary>
    public const string Timeout = "Timeout";

    /// <summary>The result (or inputs) cannot cross as JSON, or is larger than maxResultBytes.</summary>
    public const string InvalidResult = "InvalidResult";

    /// <summary>Code.Python ran without the plugin's pythonPath setting.</summary>
    public const string PythonNotConfigured = "PythonNotConfigured";

    /// <summary>A property has a value of the wrong kind.</summary>
    public const string InvalidInput = "InvalidInput";
}

/// <summary>
/// <c>Code.JavaScript</c>: a JavaScript function body run in a sandbox (ADR-0045). Jint interprets it with no access to
/// .NET, files, the network or processes, within time, statement, memory and recursion limits. Values cross only as
/// JSON: <c>inputs</c> in, the returned value out.
/// </summary>
public sealed class JavaScriptActivity(ScriptingOptions options) : IActivity
{
    /// <summary>The script is wrapped in one line before it, so reported lines are one more than the user's.</summary>
    private const int WrapperLines = 1;

    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Code.JavaScript"),
        "Run JavaScript",
        Scripts.Category,
        "Runs JavaScript in a sandbox: the code is a function body that reads inputs and returns a value, such as return inputs.lines.filter(l => l.startsWith('INV'));. It has no access to files, the network or the computer, and stops past its time, statement and memory limits. Values cross as JSON: texts, numbers, true/false, null, Lists and Dictionaries.",
        [
            Scripts.Code("The JavaScript: a function body that uses inputs and ends with return value."),
            Scripts.Inputs(),
            Scripts.Timeout(30_000),
            Scripts.Result(),
        ])
    { SideEffects = ActivitySideEffects.None };

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var code = context.GetText("code");
        var inputs = Scripts.InputsJson(context, options);
        var timeout = Scripts.TimeoutOf(context, options, 30_000);
        using var engine = new Engine(o => o
            .TimeoutInterval(timeout)
            .MaxStatements(options.MaxStatements)
            .LimitMemory(options.MaxMemoryBytes)
            .LimitRecursion(options.MaxRecursion)
            .CancellationToken(context.CancellationToken)
            .DisableStringCompilation());

        // Only a string crosses into the engine; no .NET object is ever exposed to the script.
        engine.SetValue("__myrpaInputs", inputs);
        string? json;
        try
        {
            var value = engine.Evaluate($"JSON.stringify((function (inputs) {{\n{code}\n}})(JSON.parse(__myrpaInputs)))");
            json = value.IsString() ? value.AsString() : null;
        }
        catch (ExecutionCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(context.CancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or ExecutionCanceledException)
        {
            throw new ActivityFailedException(ScriptErrorTypes.Timeout, $"The JavaScript of {context.Node.Type} '{context.Node.Id}' ran longer than {timeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms.", ex);
        }
        catch (Exception ex) when (ex is StatementsCountOverflowException or MemoryLimitExceededException or RecursionDepthOverflowException)
        {
            var limit = ex switch
            {
                StatementsCountOverflowException => $"the statement limit ({options.MaxStatements}, setting maxStatements)",
                MemoryLimitExceededException => $"the memory limit ({options.MaxMemoryBytes} bytes, setting maxMemoryBytes)",
                _ => $"the recursion limit ({options.MaxRecursion} calls deep, setting maxRecursion)",
            };
            throw new ActivityFailedException(ScriptErrorTypes.ScriptLimit, $"The JavaScript of {context.Node.Type} '{context.Node.Id}' went past {limit}.", ex);
        }
        catch (Acornima.ParseErrorException ex)
        {
            throw new ActivityFailedException(ScriptErrorTypes.ScriptSyntax, $"The JavaScript of {context.Node.Type} '{context.Node.Id}' does not parse{Line(ex)}: {ex.Description}", ex);
        }
        catch (JavaScriptException ex)
        {
            var syntax = ex.Error.IsObject() && ex.Error.AsObject().Get("name").ToString() == "SyntaxError";
            throw new ActivityFailedException(
                syntax ? ScriptErrorTypes.ScriptSyntax : ScriptErrorTypes.ScriptError,
                $"The JavaScript of {context.Node.Type} '{context.Node.Id}' {(syntax ? "does not parse" : "failed")}{Line(ex)}: {ex.Message}",
                ex);
        }

        Scripts.SetResult(context, options, json, "JavaScript");
        return ActivityResult.CompletedTask;
    }

    /// <summary>" (line N)" in the user's code, when the engine knows where.</summary>
    private static string Line(Exception ex)
    {
        if (ex is Acornima.ParseErrorException parse && parse.LineNumber > WrapperLines)
        {
            return $" (line {parse.LineNumber - WrapperLines})";
        }

        return JintException.TryGetJavaScriptLocation(ex, out var location) && location.Start.Line > WrapperLines
            ? $" (line {location.Start.Line - WrapperLines})"
            : string.Empty;
    }
}

/// <summary>
/// <c>Code.Python</c>: a Python function body run by the operator's interpreter in a separate process (ADR-0045). Not a
/// sandbox: the script can do what the robot's account can, so the plugin runs it only when the operator sets
/// <c>pythonPath</c>. Values cross as JSON: <c>inputs</c> on standard input, the returned value through a private file.
/// </summary>
public sealed class PythonActivity(ScriptingOptions options) : IActivity
{
    /// <summary>Lines before the user's code in the generated script (reported lines are this much larger).</summary>
    private const int WrapperLines = 2;

    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Code.Python"),
        "Run Python",
        Scripts.Category,
        "Runs Python with the interpreter the operator configured (the scripting plugin's pythonPath; without it this activity fails). The code is a function body that reads inputs and returns a value, such as return [l for l in inputs['lines'] if l.startswith('INV')]. Not a sandbox: the script can use files, the network and installed packages. Values cross as JSON; what the script prints is in output.",
        [
            Scripts.Code("The Python: a function body that uses inputs (a dict) and ends with return value. Indent with spaces."),
            Scripts.Inputs(),
            Scripts.Timeout(60_000),
            Scripts.Result(),
            new("output", ActivityPropertyKind.AssignmentTarget, isRequired: false, "Receives what the script printed (standard output).") { ValueType = ActivityValueType.String },
        ])
    { SideEffects = ActivitySideEffects.FileSystem | ActivitySideEffects.Network };

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (options.PythonPath is not { } python)
        {
            throw new ActivityFailedException(ScriptErrorTypes.PythonNotConfigured, $"{context.Node.Type} needs the scripting plugin's pythonPath setting (the operator enables Python by naming its interpreter); it is not set.");
        }

        var code = context.GetText("code");
        var inputs = Scripts.InputsJson(context, options);
        var timeout = Scripts.TimeoutOf(context, options, 60_000);
        var folder = Directory.CreateTempSubdirectory("myrpa-python-").FullName;
        try
        {
            var script = Path.Combine(folder, "script.py");
            var resultFile = Path.Combine(folder, "result.json");
            await File.WriteAllTextAsync(script, Wrap(code), new UTF8Encoding(false), context.CancellationToken).ConfigureAwait(false);

            var start = new ProcessStartInfo(python)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = options.Files?.Root ?? folder,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in new[] { "-I", "-X", "utf8", script, resultFile })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new ActivityFailedException(ScriptErrorTypes.ScriptError, "The Python interpreter could not be started.");
            var output = ReadAsync(process.StandardOutput, options.MaxOutputBytes);
            var errors = ReadAsync(process.StandardError, options.MaxOutputBytes);
            try
            {
                await process.StandardInput.WriteAsync(inputs.AsMemory(), context.CancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The script ended without reading its inputs; its exit code and errors tell why.
            }

            using var timer = new CancellationTokenSource(timeout, context.TimeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, timer.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                context.CancellationToken.ThrowIfCancellationRequested();
                throw new ActivityFailedException(ScriptErrorTypes.Timeout, $"The Python of {context.Node.Type} '{context.Node.Id}' ran longer than {timeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms and was stopped.");
            }

            var printed = await output.ConfigureAwait(false);
            var stderr = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var syntax = Regex.IsMatch(stderr, @"^(SyntaxError|IndentationError|TabError)\b", RegexOptions.Multiline, TimeSpan.FromSeconds(1));
                throw new ActivityFailedException(
                    syntax ? ScriptErrorTypes.ScriptSyntax : ScriptErrorTypes.ScriptError,
                    $"The Python of {context.Node.Type} '{context.Node.Id}' {(syntax ? "does not parse" : "failed")}{Line(stderr)}: {LastLine(stderr)}");
            }

            string? json = null;
            if (File.Exists(resultFile))
            {
                if (new FileInfo(resultFile).Length > options.MaxResultBytes)
                {
                    throw new ActivityFailedException(ScriptErrorTypes.InvalidResult, $"The result of {context.Node.Type} '{context.Node.Id}' is larger than {options.MaxResultBytes} bytes (setting maxResultBytes).");
                }

                json = await File.ReadAllTextAsync(resultFile, Encoding.UTF8, context.CancellationToken).ConfigureAwait(false);
            }

            Scripts.SetResult(context, options, json, "Python");
            if (context.HasProperty("output"))
            {
                context.SetValue(context.GetName("output"), printed);
            }

            return ActivityResult.Completed;
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a temporary folder left behind holds only this run's script and result.
            }
        }
    }

    /// <summary>The user's body inside a function; the result goes to the file named by the first argument.</summary>
    private static string Wrap(string code)
    {
        var body = string.Join('\n', code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => "    " + line));
        return "import json as __myrpa_json, sys as __myrpa_sys\n"
            + "def __myrpa_main(inputs):\n"
            + body + "\n"
            + "    pass\n"
            + "__myrpa_result = __myrpa_main(__myrpa_json.load(__myrpa_sys.stdin))\n"
            + "with open(__myrpa_sys.argv[1], 'w', encoding='utf-8') as __myrpa_file:\n"
            + "    __myrpa_json.dump(__myrpa_result, __myrpa_file, ensure_ascii=False, default=str)\n";
    }

    /// <summary>Reads a stream to its end, keeping at most <paramref name="limit"/> characters.</summary>
    private static async Task<string> ReadAsync(StreamReader reader, int limit)
    {
        var kept = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            var room = limit - kept.Length;
            if (room > 0)
            {
                kept.Append(buffer, 0, Math.Min(room, read));
            }
        }

        return kept.ToString();
    }

    /// <summary>" (line N)" of the user's code from a traceback (its last frame in the script).</summary>
    private static string Line(string stderr)
    {
        var matches = Regex.Matches(stderr, @"script\.py"", line (\d+)", RegexOptions.None, TimeSpan.FromSeconds(1));
        return matches.Count > 0 && int.TryParse(matches[^1].Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var line) && line > WrapperLines
            ? $" (line {line - WrapperLines})"
            : string.Empty;
    }

    /// <summary>The error itself: the last non-empty line of the error output, shortened.</summary>
    private static string LastLine(string stderr)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no error output";
        return line.Length > 300 ? line[..300] + "…" : line;
    }
}

/// <summary>Properties and value exchange shared by the scripting activities.</summary>
internal static class Scripts
{
    public const string Category = "Code";

    public static ActivityPropertyDefinition Code(string description) =>
        new("code", ActivityPropertyKind.Text, isRequired: true, description) { ValueType = ActivityValueType.String, IsMultiline = true };

    public static ActivityPropertyDefinition Inputs() =>
        new("inputs", ActivityPropertyKind.Expression, isRequired: false, "The values the script reads as inputs: a Dictionary of name → value, such as { 'lines': lines, 'rate': 0.2 }.") { ValueType = ActivityValueType.Dictionary };

    public static ActivityPropertyDefinition Timeout(int defaultMs) =>
        new("timeoutMs", ActivityPropertyKind.Expression, isRequired: false, "How long the script may run, in milliseconds (also capped by the plugin's maxTimeoutMs and the run's deadline).") { ValueType = ActivityValueType.Int, DefaultValue = defaultMs.ToString(CultureInfo.InvariantCulture) };

    public static ActivityPropertyDefinition Result() =>
        new("result", ActivityPropertyKind.AssignmentTarget, isRequired: false, "Receives the returned value (null when the script returns nothing).") { ValueType = ActivityValueType.Any };

    /// <summary>The inputs as JSON text (an empty object when absent).</summary>
    public static string InputsJson(IActivityContext context, ScriptingOptions options)
    {
        if (!context.HasProperty("inputs"))
        {
            return "{}";
        }

        var value = context.Evaluate("inputs") as IReadOnlyDictionary<string, object?>
            ?? throw new ActivityFailedException(ScriptErrorTypes.InvalidInput, $"'inputs' of {context.Node.Type} '{context.Node.Id}' must be a Dictionary of names and values.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WorkflowValues.WriteJson(writer, value);
        }

        return stream.Length <= options.MaxResultBytes
            ? Encoding.UTF8.GetString(stream.ToArray())
            : throw new ActivityFailedException(ScriptErrorTypes.InvalidResult, $"The inputs of {context.Node.Type} '{context.Node.Id}' are larger than {options.MaxResultBytes} bytes (setting maxResultBytes).");
    }

    /// <summary>The timeout: the property (or its default), capped by the plugin and the run's deadline.</summary>
    public static TimeSpan TimeoutOf(IActivityContext context, ScriptingOptions options, int defaultMs)
    {
        var milliseconds = !context.HasProperty("timeoutMs") ? defaultMs
            : context.Evaluate("timeoutMs") is long value && value > 0 ? value
            : throw new ActivityFailedException(ScriptErrorTypes.InvalidInput, $"'timeoutMs' of {context.Node.Type} '{context.Node.Id}' must be a positive whole number of milliseconds.");
        var timeout = TimeSpan.FromMilliseconds(Math.Min(milliseconds, options.MaxTimeoutMs));
        if (context.Deadline is { } deadline && deadline - context.TimeProvider.GetUtcNow() < timeout)
        {
            var remaining = deadline - context.TimeProvider.GetUtcNow();
            timeout = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
        }

        return timeout;
    }

    /// <summary>Turns the script's JSON result into a workflow value and assigns it.</summary>
    public static void SetResult(IActivityContext context, ScriptingOptions options, string? json, string language)
    {
        object? value = null;
        if (json is not null)
        {
            if (json.Length > options.MaxResultBytes)
            {
                throw new ActivityFailedException(ScriptErrorTypes.InvalidResult, $"The result of {context.Node.Type} '{context.Node.Id}' is larger than {options.MaxResultBytes} bytes (setting maxResultBytes).");
            }

            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
                if (!WorkflowValues.TryFromJsonUntyped(document.RootElement, out value, out var error))
                {
                    throw new ActivityFailedException(ScriptErrorTypes.InvalidResult, $"The {language} result of {context.Node.Type} '{context.Node.Id}' cannot become a workflow value: {error}");
                }
            }
            catch (JsonException ex)
            {
                throw new ActivityFailedException(ScriptErrorTypes.InvalidResult, $"The {language} result of {context.Node.Type} '{context.Node.Id}' is not JSON (nested deeper than 64 levels, or not a value).", ex);
            }
        }

        if (context.HasProperty("result"))
        {
            context.SetValue(context.GetName("result"), value);
        }
    }
}
