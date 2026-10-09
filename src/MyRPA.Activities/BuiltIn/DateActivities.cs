using System.Globalization;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;
using static MyRPA.Activities.BuiltIn.BuiltInProperties;

namespace MyRPA.Activities.BuiltIn;

/// <summary>Date and time activities (Phase 7, ADR-0042). Formats are .NET custom formats with the invariant culture.</summary>
public static class DateActivities
{
    /// <summary>Category of the date and time activities.</summary>
    public const string Category = "Date and Time";

    internal static ActivityPropertyDefinition TimeZoneProperty(string description) =>
        Input("timeZone", ActivityValueType.String, description + " An IANA id such as Europe/Paris; UTC when omitted.");
}

/// <summary><c>Core.Date.Parse</c>: text to a date and time.</summary>
public sealed class DateParseActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Date.Parse"),
        "Parse Date",
        DateActivities.Category,
        "Reads a date and time from text. With format, the text must match it exactly (e.g. dd/MM/yyyy HH:mm); without, ISO 8601 and invariant forms are read. Text without an offset is taken in timeZone. Fails with InvalidFormat when the text does not match.",
        [
            Input("text", ActivityValueType.String, "The text, e.g. 31/12/2026 or 2026-12-31T10:00:00+01:00.", required: true),
            Input("format", ActivityValueType.String, "The exact .NET date format the text has."),
            DateActivities.TimeZoneProperty("Where the time is when the text has no offset."),
            Result(ActivityValueType.DateTime, "Receives the date and time."),
        ]);

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var text = Text(context, "text").Trim();
        var format = OptionalText(context, "format");
        var zone = Zone(OptionalText(context, "timeZone"));
        var hasOffset = format is null ? HasOffset(text) : format.Contains('z', StringComparison.Ordinal) || format.Contains('K', StringComparison.Ordinal);
        DateTimeOffset result;
        if (hasOffset)
        {
            var parsed = format is null
                ? DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out result)
                : DateTimeOffset.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
            if (!parsed)
            {
                throw Mismatch(context, format);
            }
        }
        else
        {
            var parsed = format is null
                ? DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
                : DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out local);
            if (!parsed)
            {
                throw Mismatch(context, format);
            }

            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local))
            {
                throw new ActivityFailedException("InvalidInput", $"The time read by '{context.Node.Id}' does not exist in {zone.Id} (a daylight-saving gap).");
            }

            result = new DateTimeOffset(local, zone.GetUtcOffset(local));
        }

        SetResult(context, result);
        return ActivityResult.CompletedTask;
    }

    private static bool HasOffset(string text) =>
        text.EndsWith('Z') || (text.Length > 6 && (text[^6] is '+' or '-') && text[^3] == ':');

    private static ActivityFailedException Mismatch(IActivityContext context, string? format) =>
        new("InvalidFormat", format is null
            ? $"'text' of '{context.Node.Id}' is not a date and time this activity can read; give its format."
            : $"'text' of '{context.Node.Id}' does not match the format '{format}'.");
}

/// <summary><c>Core.Date.Format</c>: a date and time to text.</summary>
public sealed class DateFormatActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Date.Format"),
        "Format Date",
        DateActivities.Category,
        "Writes a date and time as text in a .NET format (e.g. yyyy-MM-dd, dd MMM yyyy HH:mm), shown in a time zone. Without a format it writes ISO 8601.",
        [
            Input("value", ActivityValueType.DateTime, "The date and time.", required: true),
            Input("format", ActivityValueType.String, "The .NET date format.", defaultJson: "\"yyyy-MM-ddTHH:mm:sszzz\""),
            DateActivities.TimeZoneProperty("Where the time is shown."),
            Result(ActivityValueType.String, "Receives the text."),
        ]);

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var value = TimeZoneInfo.ConvertTime(Date(context, "value"), Zone(OptionalText(context, "timeZone")));
        var format = OptionalText(context, "format") ?? "yyyy-MM-ddTHH:mm:sszzz";
        try
        {
            SetResult(context, value.ToString(format, CultureInfo.InvariantCulture));
        }
        catch (FormatException ex)
        {
            throw new ActivityFailedException("InvalidFormat", $"'format' of '{context.Node.Id}' is not a valid date format.", ex);
        }

        return ActivityResult.CompletedTask;
    }
}

/// <summary><c>Core.Date.Add</c>: moves a date and time.</summary>
public sealed class DateAddActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Date.Add"),
        "Add to Date",
        DateActivities.Category,
        "Adds (or, with negative numbers, subtracts) months, days, hours, minutes and seconds to a date and time. Months keep the day where possible (31 Jan + 1 month = 28 or 29 Feb).",
        [
            Input("value", ActivityValueType.DateTime, "The date and time.", required: true),
            Input("months", ActivityValueType.Int, "Months to add.", defaultJson: "0"),
            Input("days", ActivityValueType.Int, "Days to add.", defaultJson: "0"),
            Input("hours", ActivityValueType.Int, "Hours to add.", defaultJson: "0"),
            Input("minutes", ActivityValueType.Int, "Minutes to add.", defaultJson: "0"),
            Input("seconds", ActivityValueType.Int, "Seconds to add.", defaultJson: "0"),
            Result(ActivityValueType.DateTime, "Receives the new date and time."),
        ]);

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var value = Date(context, "value").AddMonths(checked((int)Number(context, "months", 0)))
                + TimeSpan.FromDays(Number(context, "days", 0))
                + TimeSpan.FromHours(Number(context, "hours", 0))
                + TimeSpan.FromMinutes(Number(context, "minutes", 0))
                + TimeSpan.FromSeconds(Number(context, "seconds", 0));
            SetResult(context, value);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
        {
            throw new ActivityFailedException("InvalidInput", $"The date of '{context.Node.Id}' moved outside the supported range.", ex);
        }

        return ActivityResult.CompletedTask;
    }
}

/// <summary><c>Core.Date.Difference</c>: the time between two dates.</summary>
public sealed class DateDifferenceActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Date.Difference"),
        "Date Difference",
        DateActivities.Category,
        "The time from one date and time to another, in days, hours, minutes or seconds (negative when 'to' is earlier).",
        [
            Input("from", ActivityValueType.DateTime, "The start.", required: true),
            Input("to", ActivityValueType.DateTime, "The end.", required: true),
            Choice("unit", "The unit of the result.", "Days", "Days", "Hours", "Minutes", "Seconds"),
            Result(ActivityValueType.Decimal, "Receives the difference (a Decimal)."),
        ]);

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var span = Date(context, "to") - Date(context, "from");
        var amount = context.GetTextOrDefault("unit", "Days") switch
        {
            "Hours" => span.TotalHours,
            "Minutes" => span.TotalMinutes,
            "Seconds" => span.TotalSeconds,
            _ => span.TotalDays,
        };
        SetResult(context, Math.Round((decimal)amount, 6));
        return ActivityResult.CompletedTask;
    }
}
