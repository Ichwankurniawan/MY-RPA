using MyRPA.Core.Diagnostics;

namespace MyRPA.Runtime.Diagnostics;

/// <summary>Opens <see cref="IExecutionScope"/> instances. The Phase 2 engine opens one per execution and per node.</summary>
public interface IExecutionScopeFactory
{
    /// <summary>Opens a scope: begins a logger scope and starts a span named <paramref name="operationName"/>.</summary>
    /// <param name="identity">Identifiers to attach to logs and traces.</param>
    /// <param name="operationName">Span name, e.g. <c>workflow.execute</c>.</param>
    IExecutionScope Begin(ExecutionIdentity identity, string operationName);
}
