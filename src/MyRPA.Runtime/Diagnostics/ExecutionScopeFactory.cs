using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MyRPA.Core.Diagnostics;

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
    public IExecutionScope Begin(ExecutionIdentity identity, string operationName)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        var tags = identity.ToTags();
        var logScope = _logger.BeginScope(new ExecutionScopeState(tags));
        var activity = _telemetry.ActivitySource.StartActivity(
            operationName,
            ActivityKind.Internal,
            parentContext: default,
            tags: tags);

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

        public void Dispose()
        {
            Activity?.Dispose();
            logScope?.Dispose();
        }
    }
}
