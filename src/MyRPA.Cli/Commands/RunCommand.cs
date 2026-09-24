using System.Globalization;
using System.Text.Json;
using MyRPA.Core.Execution;
using MyRPA.Core.Identifiers;
using MyRPA.Storage;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Serialization;
using MyRPA.Workflow.Values;

namespace MyRPA.Cli.Commands;

/// <summary>
/// <c>myrpa run &lt;workflow.json&gt; [--arg name=value]... [--timeout seconds] [--correlation-id id]</c>:
/// load → validate → execute → print the execution result as JSON on stdout.
/// Exit codes: 0 succeeded, 1 failed, 3 invalid workflow, 4 timed out, 130 cancelled, 2 usage.
/// </summary>
/// <param name="output">CLI output writers.</param>
/// <param name="files">Workflow file loader.</param>
/// <param name="runner">The workflow engine.</param>
public sealed class RunCommand(CliOutput output, WorkflowFileLoader files, IWorkflowRunner runner) : ICliCommand
{
    /// <inheritdoc />
    public string Name => "run";

    /// <inheritdoc />
    public string Usage => "run <workflow.json> [--arg name=value]... [--timeout seconds] [--correlation-id id]";

    /// <inheritdoc />
    public string Description => "Validate and execute a workflow; prints the result as JSON.";

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!TryParseOptions(arguments, out var options, out var usageError))
        {
            await output.Error.WriteLineAsync($"Error: {usageError}").ConfigureAwait(false);
            await output.Error.WriteLineAsync($"Usage: myrpa {Usage}").ConfigureAwait(false);
            return CliExitCodes.Usage;
        }

        var resolution = await files.LoadAsync(options.Path, cancellationToken).ConfigureAwait(false);
        foreach (var diagnostic in resolution.Diagnostics)
        {
            await output.Error.WriteLineAsync(diagnostic.ToString()).ConfigureAwait(false);
        }

        if (!resolution.Succeeded)
        {
            await output.Error.WriteLineAsync($"Error: {resolution.Error} The workflow was not executed.").ConfigureAwait(false);
            return resolution.Diagnostics.Count == 0 ? CliExitCodes.Usage : CliExitCodes.InvalidWorkflow;
        }

        var workflow = resolution.Workflow;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, text) in options.Arguments)
        {
            var argument = workflow.Arguments.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
            if (argument is null || !argument.IsInput)
            {
                await output.Error.WriteLineAsync($"Error: workflow '{workflow.Id}' has no input argument '{name}'.").ConfigureAwait(false);
                return CliExitCodes.Usage;
            }

            if (!WorkflowValues.TryParseText(text, argument.Type, out var value, out var parseError))
            {
                await output.Error.WriteLineAsync($"Error: --arg {name}: {parseError}").ConfigureAwait(false);
                return CliExitCodes.Usage;
            }

            values[name] = value;
        }

        var result = await runner.RunAsync(
            workflow,
            new WorkflowRunRequest
            {
                Arguments = values,
                CorrelationId = options.CorrelationId,
                Timeout = options.Timeout,
                Location = resolution.Location,
            },
            cancellationToken).ConfigureAwait(false);

        await output.Out.WriteLineAsync(ToJson(result)).ConfigureAwait(false);
        return result.Status switch
        {
            ExecutionStatus.Succeeded => CliExitCodes.Success,
            ExecutionStatus.TimedOut => CliExitCodes.TimedOut,
            ExecutionStatus.Cancelled => CliExitCodes.Cancelled,
            _ => CliExitCodes.Failure,
        };
    }

    /// <summary>Serializes an execution result as indented JSON.</summary>
    /// <param name="result">The result.</param>
    public static string ToJson(WorkflowExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WorkflowJsonWriter.Options))
        {
            writer.WriteStartObject();
            writer.WriteString("executionId", result.ExecutionId.ToString());
            writer.WriteString("correlationId", result.CorrelationId.Value);
            writer.WriteString("workflowId", result.WorkflowId.Value);
            writer.WriteString("status", result.Status.ToString());
            writer.WriteString("startedAt", result.StartedAt.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteNumber("durationMs", (long)result.Duration.TotalMilliseconds);
            writer.WritePropertyName("outputs");
            WorkflowValues.WriteJson(writer, result.Outputs);
            if (result.Error is { } error)
            {
                writer.WriteStartObject("error");
                writer.WriteString("code", error.Code);
                writer.WriteString("message", error.Message);
                writer.WriteString("nodeId", error.NodeId);
                writer.WriteString("activityType", error.ActivityType);
                writer.WriteString("errorType", error.ErrorType);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("error");
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool TryParseOptions(IReadOnlyList<string> arguments, out RunOptions options, out string error)
    {
        options = new RunOptions();
        error = string.Empty;
        string? path = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            var current = arguments[i];
            string? NextValue() => i + 1 < arguments.Count ? arguments[++i] : null;

            switch (current)
            {
                case "--arg":
                    var pair = NextValue();
                    var separator = pair?.IndexOf('=', StringComparison.Ordinal) ?? -1;
                    if (pair is null || separator <= 0)
                    {
                        error = "--arg expects name=value.";
                        return false;
                    }

                    var name = pair[..separator];
                    if (options.Arguments.ContainsKey(name))
                    {
                        error = $"--arg {name} is given more than once.";
                        return false;
                    }

                    options.Arguments[name] = pair[(separator + 1)..];
                    break;
                case "--timeout":
                    if (!double.TryParse(NextValue(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
                        || seconds <= 0 || seconds > TimeSpan.FromDays(1).TotalSeconds)
                    {
                        error = "--timeout expects a positive number of seconds (at most one day).";
                        return false;
                    }

                    options.Timeout = TimeSpan.FromSeconds(seconds);
                    break;
                case "--correlation-id":
                    if (!CorrelationId.TryCreate(NextValue(), out var correlation))
                    {
                        error = "--correlation-id expects 1-128 characters from [A-Za-z0-9-_.:].";
                        return false;
                    }

                    options.CorrelationId = correlation;
                    break;
                default:
                    if (current.StartsWith("--", StringComparison.Ordinal))
                    {
                        error = $"Unknown option '{current}'.";
                        return false;
                    }

                    if (path is not null)
                    {
                        error = "Only one workflow file can be run.";
                        return false;
                    }

                    path = current;
                    break;
            }
        }

        if (path is null)
        {
            error = "Missing workflow file.";
            return false;
        }

        options.Path = path;
        return true;
    }

    private sealed class RunOptions
    {
        public string Path { get; set; } = string.Empty;

        public Dictionary<string, string> Arguments { get; } = new(StringComparer.Ordinal);

        public TimeSpan? Timeout { get; set; }

        public CorrelationId? CorrelationId { get; set; }
    }
}
