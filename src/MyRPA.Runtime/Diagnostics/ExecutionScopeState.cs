using System.Collections;

namespace MyRPA.Runtime.Diagnostics;

/// <summary>
/// Logger scope state: exposes the identity as key/value pairs for structured providers and renders
/// <c>key=value</c> pairs for text providers such as the console.
/// </summary>
internal sealed class ExecutionScopeState(IReadOnlyList<KeyValuePair<string, object?>> tags)
    : IReadOnlyList<KeyValuePair<string, object?>>
{
    public int Count => tags.Count;

    public KeyValuePair<string, object?> this[int index] => tags[index];

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => tags.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => string.Join(", ", tags.Select(t => $"{t.Key}={t.Value}"));
}
