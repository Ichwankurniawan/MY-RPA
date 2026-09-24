using System.Diagnostics;
using MyRPA.Core.Diagnostics;
using MyRPA.Core.Execution;

namespace MyRPA.Runtime.Diagnostics;

/// <summary>
/// A unit of observed work: while it is open, log entries carry the <see cref="Identity"/> as scope values and a
/// trace span (when anything listens) carries the same values as tags. Call <see cref="Complete"/> to record the
/// outcome, then dispose to close both.
/// </summary>
public interface IExecutionScope : IDisposable
{
    /// <summary>The identity attached to logs and traces.</summary>
    ExecutionIdentity Identity { get; }

    /// <summary>The trace span, or <see langword="null"/> when no listener is sampling the runtime source.</summary>
    Activity? Activity { get; }

    /// <summary>
    /// Records the outcome on the span: status (Ok for success, Error for failure/timeout, Unset for cancellation),
    /// the <c>myrpa.outcome</c> tag, and the exception as a span event.
    /// </summary>
    /// <param name="status">Outcome.</param>
    /// <param name="exception">Failure cause, if any.</param>
    void Complete(ExecutionStatus status, Exception? exception = null);
}
