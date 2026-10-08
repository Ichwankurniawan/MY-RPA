using MyRPA.Contracts.Execution;
using MyRPA.Workflow.Execution;

namespace MyRPA.Execution.Hosting;

/// <summary>How to debug a run (ADR-0040).</summary>
public sealed record DebugOptions
{
    /// <summary>Nodes to pause before.</summary>
    public IReadOnlyCollection<DebugBreakpoint> Breakpoints { get; init; } = [];

    /// <summary>Pause before the first node (reason <see cref="DebugPauseReason.Pause"/>).</summary>
    public bool PauseAtStart { get; init; }
}

/// <summary>A node to pause before: its workflow's id and its node id.</summary>
/// <param name="WorkflowId">The workflow's id (the entry workflow or one it invokes).</param>
/// <param name="NodeId">The node's id.</param>
public sealed record DebugBreakpoint(string WorkflowId, string NodeId);

/// <summary>Why a debugged run paused.</summary>
public enum DebugPauseReason
{
    /// <summary>The node has a breakpoint.</summary>
    Breakpoint = 0,

    /// <summary>A step command reached its next node.</summary>
    Step = 1,

    /// <summary>Pause was requested (or the run was started paused).</summary>
    Pause = 2,
}

/// <summary>How a paused run continues.</summary>
public enum DebugCommand
{
    /// <summary>Run until the next breakpoint or a pause request.</summary>
    Continue = 0,

    /// <summary>Pause before the next node, at any depth.</summary>
    StepInto = 1,

    /// <summary>Pause before the next node at the paused node's depth or shallower (its children run through).</summary>
    StepOver = 2,

    /// <summary>Pause before the next node shallower than the paused node (the rest of its parent runs through).</summary>
    StepOut = 3,
}

/// <summary>Where a debugged run is paused, with the values in scope there.</summary>
/// <param name="ExecutionId">The execution the node belongs to (the run's, or an invoked workflow's).</param>
/// <param name="ParentExecutionId">The invoking execution, for a node of an invoked workflow.</param>
/// <param name="WorkflowId">The node's workflow.</param>
/// <param name="NodeId">The node about to start.</param>
/// <param name="ActivityType">The node's activity type.</param>
/// <param name="Depth">Nesting depth in the whole run.</param>
/// <param name="Reason">Why it paused.</param>
/// <param name="Values">Arguments, variables and locals in scope (canonical values). Never part of an event.</param>
public sealed record DebugPause(
    string ExecutionId,
    string? ParentExecutionId,
    string WorkflowId,
    string NodeId,
    string ActivityType,
    int Depth,
    DebugPauseReason Reason,
    IReadOnlyList<DebugValue> Values);

/// <summary>
/// The debugger of one run (ADR-0040): its breakpoints, where it is paused, and the commands that continue it. Pausing
/// and resuming append <see cref="ExecutionEventKinds.DebugPaused"/> / <see cref="ExecutionEventKinds.DebugResumed"/> to
/// the run's events, without values; <see cref="Paused"/> holds the values. Stopping a run is
/// <see cref="ExecutionHost.Cancel"/>, which also ends a pause.
/// </summary>
public sealed class DebugSession : IExecutionDebugger
{
    private readonly Lock _gate = new();
    private readonly ExecutionRecord _record;
    private readonly TimeProvider _time;
    private HashSet<DebugBreakpoint> _breakpoints;
    private Mode _mode = Mode.Run;
    private int _stepDepth;
    private bool _pauseRequested;
    private bool _ended;
    private DebugPause? _paused;
    private TaskCompletionSource? _resume;

    internal DebugSession(ExecutionRecord record, DebugOptions options, TimeProvider time)
    {
        _record = record;
        _time = time;
        _breakpoints = [.. options.Breakpoints];
        _pauseRequested = options.PauseAtStart;
    }

    private enum Mode
    {
        Run,
        StepInto,
        StepOver,
        StepOut,
    }

    /// <summary>Where the run is paused, or <see langword="null"/> while it runs (and after it ended).</summary>
    public DebugPause? Paused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    /// <summary>The current breakpoints.</summary>
    public IReadOnlyCollection<DebugBreakpoint> Breakpoints
    {
        get
        {
            lock (_gate)
            {
                return [.. _breakpoints];
            }
        }
    }

    /// <summary>Replaces the breakpoints; they apply from the next node on.</summary>
    /// <param name="breakpoints">The new set.</param>
    public void SetBreakpoints(IEnumerable<DebugBreakpoint> breakpoints)
    {
        ArgumentNullException.ThrowIfNull(breakpoints);
        HashSet<DebugBreakpoint> set = [.. breakpoints];
        lock (_gate)
        {
            _breakpoints = set;
        }
    }

    /// <summary>Continues a paused run.</summary>
    /// <param name="command">How far it runs before pausing again.</param>
    /// <returns>Whether the run was paused (otherwise nothing happens).</returns>
    public bool Resume(DebugCommand command)
    {
        TaskCompletionSource? resume;
        lock (_gate)
        {
            if (_paused is not { } paused || _ended)
            {
                return false;
            }

            (_mode, _stepDepth) = command switch
            {
                DebugCommand.Continue => (Mode.Run, 0),
                DebugCommand.StepInto => (Mode.StepInto, 0),
                DebugCommand.StepOver => (Mode.StepOver, paused.Depth),
                DebugCommand.StepOut => (Mode.StepOut, paused.Depth),
                _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown debug command."),
            };
            _paused = null;
            resume = _resume;
            _resume = null;
            Append(ExecutionEventKinds.DebugResumed, paused, Name(command));
        }

        resume?.TrySetResult();
        return true;
    }

    /// <summary>Asks a running run to pause before its next node (a running activity finishes first).</summary>
    /// <returns>Whether the request was taken (not when already paused or ended).</returns>
    public bool RequestPause()
    {
        lock (_gate)
        {
            if (_ended || _paused is not null)
            {
                return false;
            }

            _pauseRequested = true;
            return true;
        }
    }

    /// <inheritdoc />
    async ValueTask IExecutionDebugger.BeforeNodeAsync(DebugStop at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(at);
        while (true)
        {
            Task resumed;
            DebugPause? mine = null;
            lock (_gate)
            {
                if (_ended)
                {
                    return;
                }

                if (_resume is not null)
                {
                    // Another node of the run is paused: everything waits, then looks again.
                    resumed = _resume.Task;
                }
                else
                {
                    if (PauseReason(at) is not { } reason)
                    {
                        return;
                    }

                    mine = new DebugPause(
                        at.Identity.ExecutionId.ToString(),
                        at.Identity.ParentExecutionId?.ToString(),
                        at.WorkflowId.Value,
                        at.Node.Id.Value,
                        at.Node.Type.Value,
                        at.Depth,
                        reason,
                        at.ReadValues());
                    _paused = mine;
                    _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _mode = Mode.Run;
                    _pauseRequested = false;
                    resumed = _resume.Task;
                    Append(ExecutionEventKinds.DebugPaused, mine, Name(reason));
                }
            }

            try
            {
                await resumed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && mine is not null)
            {
                // Stopped while paused: the run ends as Cancelled (its completion event follows).
                lock (_gate)
                {
                    if (ReferenceEquals(_paused, mine))
                    {
                        _paused = null;
                        _resume = null;
                    }
                }

                throw;
            }

            if (mine is not null)
            {
                return;
            }
        }
    }

    /// <summary>The run ended: commands no longer apply and nothing waits.</summary>
    internal void End()
    {
        TaskCompletionSource? resume;
        lock (_gate)
        {
            _ended = true;
            _paused = null;
            resume = _resume;
            _resume = null;
        }

        resume?.TrySetResult();
    }

    private DebugPauseReason? PauseReason(DebugStop at)
    {
        if (_pauseRequested)
        {
            return DebugPauseReason.Pause;
        }

        var stepped = _mode switch
        {
            Mode.StepInto => true,
            Mode.StepOver => at.Depth <= _stepDepth,
            Mode.StepOut => at.Depth < _stepDepth,
            _ => false,
        };
        if (stepped)
        {
            return DebugPauseReason.Step;
        }

        return _breakpoints.Contains(new DebugBreakpoint(at.WorkflowId.Value, at.Node.Id.Value)) ? DebugPauseReason.Breakpoint : null;
    }

    private void Append(string kind, DebugPause at, string reason) =>
        _record.AppendDebug(new ExecutionEventMessage
        {
            Sequence = 0,
            Kind = kind,
            RunId = _record.RunId,
            Time = _time.GetUtcNow(),
            ExecutionId = at.ExecutionId,
            ParentExecutionId = at.ParentExecutionId,
            WorkflowId = at.WorkflowId,
            NodeId = at.NodeId,
            ActivityType = at.ActivityType,
            Reason = reason,
        });

    private static string Name(DebugPauseReason reason) => reason switch
    {
        DebugPauseReason.Breakpoint => "breakpoint",
        DebugPauseReason.Step => "step",
        _ => "pause",
    };

    private static string Name(DebugCommand command) => command switch
    {
        DebugCommand.StepInto => "stepInto",
        DebugCommand.StepOver => "stepOver",
        DebugCommand.StepOut => "stepOut",
        _ => "continue",
    };
}
