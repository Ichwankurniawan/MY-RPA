using System.Runtime.CompilerServices;
using System.Text.Json;
using MyRPA.Contracts.Execution;
using MyRPA.Execution.Hosting;

namespace MyRPA.Server;

/// <summary>Something a tab's event stream can follow (ADR-0024): a run, or a recording (ADR-0039).</summary>
internal interface IStreamSource
{
    /// <summary>The run or recording id (random; unique across both).</summary>
    string Id { get; }

    /// <summary>The events after <paramref name="after"/>, then new ones as they come, until the source ends.</summary>
    IAsyncEnumerable<StreamEvent> ReadAsync(long after, CancellationToken cancellationToken);
}

/// <summary>One event as written to the stream: its sequence (0 for a gap notice), SSE event name and JSON data.</summary>
internal readonly record struct StreamEvent(long Sequence, string Kind, string Data);

/// <summary>A run as a stream source: its execution events, with replay and gap notices (unchanged from ADR-0024).</summary>
internal sealed class RunSource(ExecutionHandle run) : IStreamSource
{
    public string Id => run.RunId;

    public async IAsyncEnumerable<StreamEvent> ReadAsync(long after, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var message in run.ReadEventsAsync(after, cancellationToken).ConfigureAwait(false))
        {
            yield return new StreamEvent(message.Sequence, message.Kind, JsonSerializer.Serialize(message, ContractsJsonContext.Default.ExecutionEventMessage));
        }
    }
}
