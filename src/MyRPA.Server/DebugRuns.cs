using System.Collections.Concurrent;
using MyRPA.Execution.Hosting;

namespace MyRPA.Server;

/// <summary>
/// Debug runs started from the Studio (ADR-0040). Each belongs to the browser session that started it: only that session
/// may send it commands, change its breakpoints or read its paused values. Other sessions see it as unknown.
/// </summary>
internal sealed class DebugRuns(ExecutionHost host)
{
    private readonly ConcurrentDictionary<string, Owner> _owners = new(StringComparer.Ordinal);

    /// <summary>Records who started a debug run, and forgets runs the host no longer retains.</summary>
    public void Add(string runId, string session, string workflowId)
    {
        _owners[runId] = new Owner(session, workflowId);
        foreach (var known in _owners.Keys)
        {
            if (!host.TryGet(known, out _))
            {
                _owners.TryRemove(known, out _);
            }
        }
    }

    /// <summary>The run's debug session and its workflow id, when <paramref name="session"/> started it.</summary>
    public (DebugSession Debug, string WorkflowId)? Find(string runId, string session) =>
        _owners.TryGetValue(runId, out var owner) && string.Equals(owner.Session, session, StringComparison.Ordinal)
            && host.TryGet(runId, out var run) && run.Debug is { } debug
            ? (debug, owner.WorkflowId)
            : null;

    private sealed record Owner(string Session, string WorkflowId);
}
