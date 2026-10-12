namespace MyRPA.Orchestration;

/// <summary>
/// The job state machine (ADR-0049). Every state change goes through <see cref="IsAllowed"/>; a message that would make
/// an illegal change (a duplicate or stale completion, a start after cancellation) changes nothing.
/// </summary>
public static class JobTransitions
{
    private static readonly Dictionary<JobState, JobState[]> _allowed = new()
    {
        // Claimed by a Runner, or cancelled before any Runner took it.
        [JobState.Queued] = [JobState.Assigned, JobState.Cancelled],

        // Started; given back or its lease expired before start (requeued: nothing ran); cancelled or failed before start
        // (package or dependency check); out of attempts.
        [JobState.Assigned] = [JobState.Running, JobState.Queued, JobState.Cancelled, JobState.Failed],

        // The Runner's outcome, or the Runner was lost (outcome unknown).
        [JobState.Running] = [JobState.Succeeded, JobState.Failed, JobState.Cancelled, JobState.TimedOut, JobState.Interrupted],

        // Only a late completion under the same lease settles an unknown outcome; it never becomes active again.
        [JobState.Interrupted] = [JobState.Succeeded, JobState.Failed, JobState.Cancelled, JobState.TimedOut],
        [JobState.Succeeded] = [],
        [JobState.Failed] = [],
        [JobState.Cancelled] = [],
        [JobState.TimedOut] = [],
    };

    /// <summary>Whether a job may go from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static bool IsAllowed(JobState from, JobState to) => _allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>Whether <paramref name="state"/> is final (nothing can change it).</summary>
    public static bool IsFinal(JobState state) => state is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.TimedOut;

    /// <summary>The states a job may go to from <paramref name="state"/>.</summary>
    public static IReadOnlyList<JobState> Next(JobState state) => _allowed.TryGetValue(state, out var targets) ? targets : [];

    /// <summary>Parses a Runner's completion status (Succeeded, Failed, Cancelled or TimedOut).</summary>
    public static bool TryParseOutcome(string? status, out JobState state)
    {
        state = status switch
        {
            "Succeeded" => JobState.Succeeded,
            "Failed" => JobState.Failed,
            "Cancelled" => JobState.Cancelled,
            "TimedOut" => JobState.TimedOut,
            _ => JobState.Queued,
        };
        return state != JobState.Queued;
    }
}
