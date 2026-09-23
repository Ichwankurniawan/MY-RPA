using System.Diagnostics;
using MyRPA.Core.Diagnostics;

namespace MyRPA.Runtime.Diagnostics;

/// <summary>
/// A unit of observed work: while it is open, log entries carry the <see cref="Identity"/> as scope values and a
/// trace span (when anything listens) carries the same values as tags. Dispose to close both.
/// </summary>
public interface IExecutionScope : IDisposable
{
    /// <summary>The identity attached to logs and traces.</summary>
    ExecutionIdentity Identity { get; }

    /// <summary>The trace span, or <see langword="null"/> when no listener is sampling the runtime source.</summary>
    Activity? Activity { get; }
}
