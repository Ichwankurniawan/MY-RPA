using MyRPA.Core.Diagnostics;
using MyRPA.Workflow;
using MyRPA.Workflow.Execution;

namespace MyRPA.Runtime.Execution;

/// <summary>
/// One debugged run's link to its <see cref="IExecutionDebugger"/> (ADR-0040): asks it before every node and, while it
/// keeps the run paused, suspends the run's timeouts. Deadlines of a debugged run are kept net of paused time
/// (<see cref="ExecutionFrame.Deadline"/>); adding <see cref="Paused"/> turns them back into clock times.
/// </summary>
internal sealed class DebugControl(IExecutionDebugger debugger, TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly List<TrackedTimeout> _timeouts = [];
    private TimeSpan _paused;

    /// <summary>Total time the run has spent paused so far.</summary>
    public TimeSpan Paused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    /// <summary>Asks the debugger whether the node may start; waits while the run is paused.</summary>
    public async ValueTask BeforeNodeAsync(
        ExecutionFrame frame,
        NodeDefinition node,
        ExecutionIdentity identity,
        int depth,
        VariableScope variables,
        CancellationToken cancellationToken)
    {
        var stop = new DebugStop(identity, frame.Workflow.Id, node, depth, () => variables.Snapshot(frame.Workflow.SecretNames));
        var waiting = debugger.BeforeNodeAsync(stop, cancellationToken);
        if (waiting.IsCompletedSuccessfully)
        {
            return;
        }

        var pausedAt = time.GetTimestamp();
        Suspend();
        try
        {
            await waiting.ConfigureAwait(false);
        }
        finally
        {
            Resume(time.GetElapsedTime(pausedAt));
        }
    }

    /// <summary>Suspends <paramref name="source"/> (a timeout due at <paramref name="due"/>) whenever the run is paused.</summary>
    /// <returns>Stops tracking it (when its execution ends).</returns>
    public IDisposable Track(CancellationTokenSource source, DateTimeOffset due)
    {
        var timeout = new TrackedTimeout(this, source);
        lock (_gate)
        {
            timeout.NetDue = due - _paused;
            _timeouts.Add(timeout);
        }

        return timeout;
    }

    private void Suspend()
    {
        lock (_gate)
        {
            foreach (var timeout in _timeouts)
            {
                timeout.Source.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Resume(TimeSpan pausedFor)
    {
        lock (_gate)
        {
            _paused += pausedFor;
            var now = time.GetUtcNow();
            foreach (var timeout in _timeouts)
            {
                var remaining = timeout.NetDue + _paused - now;
                timeout.Source.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            }
        }
    }

    private sealed class TrackedTimeout(DebugControl owner, CancellationTokenSource source) : IDisposable
    {
        public CancellationTokenSource Source { get; } = source;

        public DateTimeOffset NetDue { get; set; }

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timeouts.Remove(this);
            }
        }
    }
}
