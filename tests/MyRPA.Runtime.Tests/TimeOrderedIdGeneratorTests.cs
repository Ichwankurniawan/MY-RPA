using System.Globalization;
using Microsoft.Extensions.Time.Testing;

namespace MyRPA.Runtime.Tests;

/// <summary>M1/M5: identifiers take their time component from the injected clock, not the system clock.</summary>
public sealed class TimeOrderedIdGeneratorTests
{
    private static long TimestampMilliseconds(string hex) =>
        long.Parse(hex[..12], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    [Fact]
    public void Ids_EmbedTheInjectedClockTime()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var ids = new TimeOrderedIdGenerator(time);

        var execution = ids.NewExecutionId().ToString();
        var correlation = ids.NewCorrelationId().Value;

        var expected = time.GetUtcNow().ToUnixTimeMilliseconds();
        Assert.Equal(expected, TimestampMilliseconds(execution));
        Assert.Equal(expected, TimestampMilliseconds(correlation));
    }

    [Fact]
    public void Ids_AreOrderedByClock_WithoutSleeping()
    {
        var time = new FakeTimeProvider();
        var ids = new TimeOrderedIdGenerator(time);

        var first = ids.NewExecutionId().ToString();
        time.Advance(TimeSpan.FromMilliseconds(1));
        var second = ids.NewExecutionId().ToString();

        Assert.True(string.CompareOrdinal(first, second) < 0);
    }

    [Fact]
    public void Ids_AreUniqueWithinTheSameMillisecond()
    {
        var ids = new TimeOrderedIdGenerator(new FakeTimeProvider());

        var generated = Enumerable.Range(0, 1000).Select(_ => ids.NewExecutionId()).ToHashSet();

        Assert.Equal(1000, generated.Count);
    }
}
