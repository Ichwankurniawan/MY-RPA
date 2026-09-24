using System.Collections;
using Microsoft.Extensions.Logging;
using MyRPA.Core.Activities;
using MyRPA.Core.Diagnostics;
using MyRPA.Workflow.Execution;

namespace MyRPA.Activities.BuiltIn;

/// <summary>
/// <c>Core.Log</c>: writes a message to the log category <see cref="DiagnosticNames.WorkflowLogCategory"/>. The
/// execution identity is attached through the ambient logging scope.
/// </summary>
/// <param name="loggerFactory">Logger factory.</param>
public sealed class LogActivity(ILoggerFactory loggerFactory) : IActivity
{
    private static readonly EventId _workflowLogEvent = new(4000, "WorkflowLog");

    private readonly ILogger _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
        .CreateLogger(DiagnosticNames.WorkflowLogCategory);

    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Log"),
        "Log",
        CoreActivities.DiagnosticsCategory,
        "Writes a message to the execution log.",
        [
            new("message", ActivityPropertyKind.Expression, isRequired: true, "Message (any value is formatted as text)."),
            new("level", ActivityPropertyKind.Text, isRequired: false, "Log level (default Information).",
                allowedValues: ["Trace", "Debug", "Information", "Warning", "Error", "Critical"]),
        ]);

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Always evaluate, so an invalid expression fails regardless of the configured log level (determinism).
        var message = context.EvaluateText("message");
        var level = Enum.Parse<LogLevel>(context.GetTextOrDefault("level", nameof(LogLevel.Information)));
        if (_logger.IsEnabled(level))
        {
            _logger.Log(level, _workflowLogEvent, new LogState(message, context.Node.Id.Value), null, static (s, _) => s.ToString());
        }

        return ActivityResult.CompletedTask;
    }

    /// <summary>Structured log state: <c>Message</c> and <c>NodeId</c> properties, formatted as the message.</summary>
    private sealed class LogState(string message, string nodeId) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly KeyValuePair<string, object?>[] _values =
        [
            new("Message", message),
            new("NodeId", nodeId),
            new("{OriginalFormat}", "{Message}"),
        ];

        public int Count => _values.Length;

        public KeyValuePair<string, object?> this[int index] => _values[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)_values).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => message;
    }
}
