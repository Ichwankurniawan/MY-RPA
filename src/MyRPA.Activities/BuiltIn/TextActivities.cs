using System.Text.RegularExpressions;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;
using static MyRPA.Activities.BuiltIn.BuiltInProperties;

namespace MyRPA.Activities.BuiltIn;

/// <summary>Text activities (Phase 7, ADR-0042): splitting, joining and regular expressions. No side effects.</summary>
public static class TextActivities
{
    /// <summary>Category of the text activities.</summary>
    public const string Category = "Text";

    /// <summary>How long one regular expression may run before the activity fails with <c>Timeout</c>.</summary>
    public static TimeSpan RegexTimeout => TimeSpan.FromSeconds(1);

    internal static Regex Pattern(IActivityContext context, bool ignoreCase)
    {
        var pattern = Text(context, "pattern");
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new ActivityFailedException("InvalidPattern", $"'pattern' of '{context.Node.Id}' is not a valid regular expression: {ex.Message}", ex);
        }
    }

    internal static ActivityFailedException TooSlow(IActivityContext context, RegexMatchTimeoutException ex) =>
        new("Timeout", $"The regular expression of '{context.Node.Id}' took longer than {RegexTimeout.TotalSeconds:0} s; simplify the pattern.", ex);
}

/// <summary><c>Core.Text.Split</c>: splits text at a separator.</summary>
public sealed class TextSplitActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Text.Split"),
        "Split Text",
        TextActivities.Category,
        "Splits text into a list at every occurrence of a separator (plain text, not a pattern). Use it to turn a comma-separated line or lines of text into a list.",
        [
            Input("text", ActivityValueType.String, "The text to split.", required: true),
            Input("separator", ActivityValueType.String, "Where to split, e.g. ',' or a line break; not empty.", required: true),
            Input("removeEmpty", ActivityValueType.Boolean, "Leave out empty parts.", defaultJson: "false"),
            Input("trim", ActivityValueType.Boolean, "Remove white space around each part.", defaultJson: "false"),
            Result(ActivityValueType.List, "Receives the parts (a List of String)."),
        ]);

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var separator = Text(context, "separator");
        if (separator.Length == 0)
        {
            throw Invalid(context, "separator", "non-empty text");
        }

        var options = (Flag(context, "removeEmpty", false) ? StringSplitOptions.RemoveEmptyEntries : StringSplitOptions.None)
            | (Flag(context, "trim", false) ? StringSplitOptions.TrimEntries : StringSplitOptions.None);
        SetResult(context, ToList(Text(context, "text").Split(separator, options)));
        return ActivityResult.CompletedTask;
    }
}

/// <summary><c>Core.Text.Join</c>: joins a list into text.</summary>
public sealed class TextJoinActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Text.Join"),
        "Join Text",
        TextActivities.Category,
        "Joins the items of a list into one text, with a separator between them. Items that are not text are written as they display.",
        [
            Input("items", ActivityValueType.List, "The items to join.", required: true),
            Input("separator", ActivityValueType.String, "Text between items.", defaultJson: "\"\""),
            Result(ActivityValueType.String, "Receives the joined text."),
        ]);

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        SetResult(context, string.Join(OptionalText(context, "separator") ?? string.Empty, List(context, "items").Select(WorkflowValues.ToDisplayString)));
        return ActivityResult.CompletedTask;
    }
}

/// <summary><c>Core.Text.Match</c>: finds a regular expression in text.</summary>
public sealed class TextMatchActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Text.Match"),
        "Match Text",
        TextActivities.Category,
        "Finds a regular expression (.NET syntax) in text and returns the match with its groups: { value, index, groups }. Use it to extract an order number, an amount or a date from text. A pattern that runs longer than 1 second fails with Timeout.",
        [
            Input("text", ActivityValueType.String, "The text to search.", required: true),
            Input("pattern", ActivityValueType.String, "The regular expression; named groups (?<name>…) become keys of groups.", required: true),
            Input("all", ActivityValueType.Boolean, "Return every match as a List instead of the first one.", defaultJson: "false"),
            Input("ignoreCase", ActivityValueType.Boolean, "Match regardless of letter case.", defaultJson: "false"),
            Result(ActivityValueType.Any, "Receives the first match (a Dictionary, or null when none), or with all: a List of matches."),
        ]);

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var regex = TextActivities.Pattern(context, Flag(context, "ignoreCase", false));
        var text = Text(context, "text");
        try
        {
            SetResult(context, Flag(context, "all", false)
                ? ToList(regex.Matches(text).Select(m => (object?)Describe(regex, m)))
                : regex.Match(text) is { Success: true } match ? Describe(regex, match) : null);
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw TextActivities.TooSlow(context, ex);
        }

        return ActivityResult.CompletedTask;
    }

    private static IReadOnlyDictionary<string, object?> Describe(Regex regex, Match match) =>
        WorkflowValues.Dictionary(
        [
            new("value", match.Value),
            new("index", (long)match.Index),
            new("groups", WorkflowValues.Dictionary(regex.GetGroupNames().Where(n => n != "0")
                .Select(n => new KeyValuePair<string, object?>(n, match.Groups[n].Success ? match.Groups[n].Value : null)))),
        ]);
}

/// <summary><c>Core.Text.Replace</c>: replaces a regular expression in text.</summary>
public sealed class TextReplaceActivity : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        CoreActivities.Name("Text.Replace"),
        "Replace Text (pattern)",
        TextActivities.Category,
        "Replaces every match of a regular expression (.NET syntax); $1 or ${name} in the replacement insert a group. For plain text, the replace() expression function is simpler.",
        [
            Input("text", ActivityValueType.String, "The text to change.", required: true),
            Input("pattern", ActivityValueType.String, "The regular expression.", required: true),
            Input("replacement", ActivityValueType.String, "What replaces each match.", required: true),
            Input("ignoreCase", ActivityValueType.Boolean, "Match regardless of letter case.", defaultJson: "false"),
            Result(ActivityValueType.String, "Receives the changed text."),
        ]);

    /// <inheritdoc />
    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var regex = TextActivities.Pattern(context, Flag(context, "ignoreCase", false));
        try
        {
            SetResult(context, regex.Replace(Text(context, "text"), Text(context, "replacement")));
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw TextActivities.TooSlow(context, ex);
        }

        return ActivityResult.CompletedTask;
    }
}
