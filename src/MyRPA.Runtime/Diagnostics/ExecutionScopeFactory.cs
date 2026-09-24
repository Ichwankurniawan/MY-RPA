using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MyRPA.Core.Diagnostics;
using MyRPA.Core.Execution;

namespace MyRPA.Runtime.Diagnostics;

/// <summary>Default <see cref="IExecutionScopeFactory"/> combining <see cref="ILogger"/> scopes and spans.</summary>
/// <param name="telemetry">Owner of the runtime activity source.</param>
/// <param name="logger">Logger whose (shared) scope provider receives the identity.</param>
public sealed partial class ExecutionScopeFactory(MyRpaTelemetry telemetry, ILogger<ExecutionScopeFactory> logger)
    : IExecutionScopeFactory
{
    private readonly MyRpaTelemetry _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    private readonly ILogger<ExecutionScopeFactory> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public IExecutionScope Begin(ExecutionIdentity identity, string operationName, IEnumerable<KeyValuePair<string, object?>>? additionalTags = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        var tags = identity.ToTags();
        var logScope = _logger.BeginScope(new ExecutionScopeState(tags));
        var activity = _telemetry.ActivitySource.StartActivity(
            operationName,
            ActivityKind.Internal,
            parentContext: default,
            tags: additionalTags is null ? tags : tags.Concat(additionalTags));

        LogScopeOpened(_logger, operationName);
        return new ExecutionScope(identity, activity, logScope);
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug, Message = "Execution scope '{OperationName}' opened")]
    private static partial void LogScopeOpened(ILogger logger, string operationName);

    private sealed class ExecutionScope(ExecutionIdentity identity, Activity? activity, IDisposable? logScope)
        : IExecutionScope
    {
        public ExecutionIdentity Identity { get; } = identity;

        public Activity? Activity { get; } = activity;

        public void Complete(ExecutionStatus status, Exception? exception = null)
        {
            if (Activity is null)
            {
                return;
            }

            Activity.SetTag(DiagnosticNames.OutcomeKey, status.ToString());
            switch (status)
            {
                case ExecutionStatus.Succeeded:
                    Activity.SetStatus(ActivityStatusCode.Ok);
                    break;
                case ExecutionStatus.Cancelled:
                    Activity.SetStatus(ActivityStatusCode.Unset);
                    break;
                default:
                    Activity.SetStatus(ActivityStatusCode.Error, exception?.Message ?? status.ToString());
                    break;
            }

            if (exception is not null && status != ExecutionStatus.Succeeded)
            {
                Activity.AddException(exception);
            }
        }

        public void Dispose()
        {
            Activity?.Dispose();
            logScope?.Dispose();
        }
    }
}
