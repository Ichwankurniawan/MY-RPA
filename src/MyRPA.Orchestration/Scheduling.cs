using System.Diagnostics.CodeAnalysis;

namespace MyRPA.Orchestration;

/// <summary>
/// A five-field cron expression (minute hour day-of-month month day-of-week), read in a time zone (ADR-0049). Fields take
/// <c>*</c>, numbers, ranges <c>a-b</c>, steps <c>*/n</c> and <c>a-b/n</c>, lists <c>a,b</c>, and month and day names
/// (<c>JAN</c>, <c>MON</c>); day-of-week 0 and 7 are Sunday. When both day fields are restricted a day matches either
/// (classic cron).
/// </summary>
/// <remarks>
/// Daylight saving: a local time skipped by a forward transition fires once, at the first valid minute after the gap; a
/// local time repeated by a backward transition fires once, at its first occurrence.
/// </remarks>
public sealed class CronExpression
{
    private static readonly string[] _months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
    private static readonly string[] _days = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _daysOfMonth;
    private readonly bool[] _monthsOfYear;
    private readonly bool[] _daysOfWeek;
    private readonly bool _anyDayOfMonth;
    private readonly bool _anyDayOfWeek;

    private CronExpression(string text, bool[] minutes, bool[] hours, bool[] daysOfMonth, bool[] months, bool[] daysOfWeek, bool anyDayOfMonth, bool anyDayOfWeek)
    {
        Text = text;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _monthsOfYear = months;
        _daysOfWeek = daysOfWeek;
        _anyDayOfMonth = anyDayOfMonth;
        _anyDayOfWeek = anyDayOfWeek;
    }

    /// <summary>The expression.</summary>
    public string Text { get; }

    /// <summary>Parses an expression.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out CronExpression? expression, [NotNullWhen(false)] out string? error)
    {
        expression = null;
        var fields = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5)
        {
            error = "A cron expression has five fields: minute hour day-of-month month day-of-week.";
            return false;
        }

        if (!TryField(fields[0], 0, 59, null, "minute", out var minutes, out error)
            || !TryField(fields[1], 0, 23, null, "hour", out var hours, out error)
            || !TryField(fields[2], 1, 31, null, "day-of-month", out var dom, out error)
            || !TryField(fields[3], 1, 12, _months, "month", out var months, out error)
            || !TryField(fields[4], 0, 7, _days, "day-of-week", out var dow, out error))
        {
            return false;
        }

        if (dow[7])
        {
            dow[0] = true;
        }

        expression = new CronExpression(string.Join(' ', fields), minutes, hours, dom, months, dow, fields[2].StartsWith('*'), fields[4].StartsWith('*'));
        return true;
    }

    /// <summary>The first fire time strictly after <paramref name="after"/>, or null within the next five years.</summary>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var localAfter = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        var start = DateTime.SpecifyKind(new DateTime(localAfter.Year, localAfter.Month, localAfter.Day, localAfter.Hour, localAfter.Minute, 0), DateTimeKind.Unspecified);
        var last = start.Date.AddYears(5);
        for (var day = start.Date; day <= last; day = day.AddDays(1))
        {
            if (!_monthsOfYear[day.Month] || !DayMatches(day))
            {
                continue;
            }

            for (var hour = 0; hour < 24; hour++)
            {
                if (!_hours[hour])
                {
                    continue;
                }

                for (var minute = 0; minute < 60; minute++)
                {
                    if (!_minutes[minute])
                    {
                        continue;
                    }

                    var local = day.AddHours(hour).AddMinutes(minute);
                    if (local < start)
                    {
                        continue;
                    }

                    var instant = ToInstant(local, zone);
                    if (instant > after)
                    {
                        return instant;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>The instant of a local time, by the daylight-saving rules above.</summary>
    public static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // Skipped by a forward transition: the first valid minute after the gap.
            var probe = local;
            while (zone.IsInvalidTime(probe))
            {
                probe = probe.AddMinutes(1);
            }

            return new DateTimeOffset(probe, zone.GetUtcOffset(probe)).ToUniversalTime();
        }

        if (zone.IsAmbiguousTime(local))
        {
            // Repeated by a backward transition: the first occurrence has the larger offset.
            return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Max()).ToUniversalTime();
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private bool DayMatches(DateTime day)
    {
        var dom = _daysOfMonth[day.Day];
        var dow = _daysOfWeek[(int)day.DayOfWeek];
        return (_anyDayOfMonth, _anyDayOfWeek) switch
        {
            (true, true) => true,
            (true, false) => dow,
            (false, true) => dom,
            _ => dom || dow,
        };
    }

    private static bool TryField(string field, int min, int max, string[]? names, string label, out bool[] values, [NotNullWhen(false)] out string? error)
    {
        values = new bool[max + 1];
        foreach (var part in field.Split(','))
        {
            var step = 1;
            var range = part;
            var slash = part.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                if (!int.TryParse(part[(slash + 1)..], out step) || step < 1)
                {
                    error = $"The {label} step in '{part}' is not a positive number.";
                    return false;
                }

                range = part[..slash];
            }

            int from, to;
            if (range == "*")
            {
                (from, to) = (min, max);
            }
            else
            {
                var dash = range.IndexOf('-', StringComparison.Ordinal);
                if (!TryValue(dash < 0 ? range : range[..dash], min, max, names, out from)
                    || !TryValue(dash < 0 ? range : range[(dash + 1)..], min, max, names, out to))
                {
                    error = $"The {label} value '{part}' is not between {min} and {max}.";
                    return false;
                }

                if (dash < 0 && slash >= 0)
                {
                    to = max;
                }

                if (from > to)
                {
                    error = $"The {label} range '{part}' runs backwards.";
                    return false;
                }
            }

            for (var v = from; v <= to; v += step)
            {
                values[v] = true;
            }
        }

        error = null;
        return true;
    }

    private static bool TryValue(string text, int min, int max, string[]? names, out int value)
    {
        if (names is not null)
        {
            var index = Array.FindIndex(names, n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                value = index + (min == 1 ? 1 : 0);
                return true;
            }
        }

        return int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
    }
}

/// <summary>Fire times of schedules (ADR-0049).</summary>
public static class ScheduleTimes
{
    /// <summary>A missed fire time older than this counts as missed (the misfire policy applies); newer ones just run late.</summary>
    public static TimeSpan MisfireGrace { get; } = TimeSpan.FromMinutes(2);

    /// <summary>Finds a time zone by IANA or Windows id.</summary>
    public static bool TryFindZone(string? id, [NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        zone = null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>Checks a schedule's definition; null when valid.</summary>
    public static string? Validate(ScheduleRecord schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (!TryFindZone(schedule.TimeZone, out _))
        {
            return $"Unknown time zone '{schedule.TimeZone}'.";
        }

        return schedule.Kind switch
        {
            ScheduleKind.Once when schedule.StartAt is null => "A one-time schedule needs a start time.",
            ScheduleKind.Interval when schedule.IntervalMinutes is not (>= 1 and <= 525_600) => "The interval must be 1 to 525,600 minutes.",
            ScheduleKind.Cron when !CronExpression.TryParse(schedule.Cron, out _, out var error) => error,
            _ => null,
        };
    }

    /// <summary>The first fire time of <paramref name="schedule"/> strictly after <paramref name="after"/> (null: none).</summary>
    public static DateTimeOffset? Next(ScheduleRecord schedule, DateTimeOffset after)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (!TryFindZone(schedule.TimeZone, out var zone))
        {
            return null;
        }

        switch (schedule.Kind)
        {
            case ScheduleKind.Once:
                return schedule.StartAt is { } once && once > after ? once : null;
            case ScheduleKind.Interval:
                var start = schedule.StartAt ?? schedule.CreatedAt;
                var interval = TimeSpan.FromMinutes(schedule.IntervalMinutes ?? 0);
                if (interval <= TimeSpan.Zero)
                {
                    return null;
                }

                if (after < start)
                {
                    return start;
                }

                var elapsed = (after - start).Ticks / interval.Ticks;
                return start + TimeSpan.FromTicks((elapsed + 1) * interval.Ticks);
            case ScheduleKind.Cron:
                if (!CronExpression.TryParse(schedule.Cron, out var cron, out _))
                {
                    return null;
                }

                var from = schedule.StartAt is { } begin && begin > after ? begin.AddTicks(-1) : after;
                return cron.Next(from, zone);
            default:
                return null;
        }
    }
}
