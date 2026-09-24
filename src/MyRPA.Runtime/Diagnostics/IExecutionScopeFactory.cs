using MyRPA.Core.Diagnostics;

namespace MyRPA.Runtime.Diagnostics;

/// <summary>Opens <see cref="IExecutionScope"/> instances. The engine opens one per execution and one per node.</summary>
public interface IExecutionScopeFactory
{
    /// <summary>Opens a scope: begins a logger scope and starts a span named <paramref name="operationName"/>.</summary>
    /// <param name="identity">Identifiers to attach to logs and traces.</param>
    /// <param name="operationName">Span name, e.g. <c>workflow.execute</c> or an activity type.</param>
    /// <param name="additionalTags">Extra span tags (not added to the log scope).</param>
    IExecutionScope Begin(ExecutionIdentity identity, string operationName, IEnumerable<KeyValuePair<string, object?>>? additionalTags = null);
}
