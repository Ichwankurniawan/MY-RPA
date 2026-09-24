using System.Globalization;
using MyRPA.Core.Identifiers;

namespace MyRPA.Runtime;

/// <summary>
/// Default <see cref="IIdGenerator"/>: UUIDv7 identifiers whose timestamp comes from the injected
/// <see cref="TimeProvider"/> (M1), so ids are time-ordered and a fake clock controls their time component.
/// </summary>
/// <param name="timeProvider">Clock.</param>
public sealed class TimeOrderedIdGenerator(TimeProvider timeProvider) : IIdGenerator
{
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc />
    public ExecutionId NewExecutionId() => new(Guid.CreateVersion7(_timeProvider.GetUtcNow()));

    /// <inheritdoc />
    public CorrelationId NewCorrelationId() =>
        new(Guid.CreateVersion7(_timeProvider.GetUtcNow()).ToString("N", CultureInfo.InvariantCulture));
}
