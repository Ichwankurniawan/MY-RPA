using System.Globalization;
using MyRPA.Core.Identifiers;

namespace MyRPA.Core.Tests;

/// <summary>Deterministic <see cref="IIdGenerator"/> for tests: 000...001, 000...002, ...</summary>
public sealed class SequentialIdGenerator : IIdGenerator
{
    private int _next;

    public ExecutionId NewExecutionId() => new(new Guid(++_next, 0, 0, new byte[8]));

    public CorrelationId NewCorrelationId() => new("corr-" + (++_next).ToString(CultureInfo.InvariantCulture));
}
