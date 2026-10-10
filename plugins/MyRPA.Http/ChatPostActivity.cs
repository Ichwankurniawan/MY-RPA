using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;
using static MyRPA.Http.HttpCore;

namespace MyRPA.Http;

/// <summary>
/// <c>Chat.Post</c>: posts a message to a Teams, Slack or Discord incoming webhook, or a generic JSON webhook (ADR-0043).
/// The webhook URL is a secret property (its path is the credential): messages name only its host, redirects are not
/// followed, and only a rate-limited attempt (429) is repeated, so a message is never posted twice.
/// </summary>
public sealed class ChatPostActivity(HttpOptions options, HttpGateway gateway) : IActivity
{
    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Chat.Post"),
        "Post Chat Message",
        "HTTP",
        "Posts a message to a Teams, Slack or Discord incoming webhook, or a generic webhook ({ title, message, color } as JSON). The webhook URL is a secret: an argument or variable, never shown in messages. A status of 400 or above fails (HttpStatus).",
        [
            new("platform", ActivityPropertyKind.Text, isRequired: true, "Where the webhook belongs.", Platforms) { ValueType = ActivityValueType.String },
            new("webhookUrl", ActivityPropertyKind.Expression, isRequired: true, "The incoming webhook URL: an argument or variable, never a value written here.") { ValueType = ActivityValueType.String, IsSecret = true },
            Input("message", ActivityValueType.String, "The message text.", required: true),
            Input("title", ActivityValueType.String, "A title shown above the message."),
            Input("color", ActivityValueType.String, "An accent colour as #RRGGBB (Slack and Discord; Teams has none)."),
            Input("timeoutMs", ActivityValueType.Int, "The timeout of each attempt in milliseconds (also capped by the run's deadline).", defaultJson: "30000"),
            Input("retries", ActivityValueType.Int, "How many times to repeat an attempt the platform rate-limited (429, 0 to 10); the server's Retry-After is honoured (up to 60 s). Other failures are not repeated, so a message is never posted twice.", defaultJson: "0"),
            Input("retryDelayMs", ActivityValueType.Int, "The wait before the first repeat when the platform gives no Retry-After, in milliseconds; it doubles for each further one.", defaultJson: "1000"),
            Output("status", ActivityValueType.Int, "Receives the status code."),
        ])
    { SideEffects = ActivitySideEffects.Network };

    private static string[] Platforms => ["Teams", "Slack", "Discord", "Generic"];

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var core = new HttpCore(options, gateway);
        var platform = context.GetText("platform");
        if (!Platforms.Contains(platform, StringComparer.Ordinal))
        {
            throw Invalid(context, "platform", string.Join(", ", Platforms));
        }

        var url = core.ParseUrl(context, "webhookUrl");
        var message = Text(context, "message");
        var title = context.HasProperty("title") ? context.Evaluate("title") as string ?? throw Invalid(context, "title", "text") : null;
        var payload = Payload(platform, message, string.IsNullOrEmpty(title) ? null : title, Color(context));
        var body = Bytes(Encoding.UTF8.GetBytes(payload.ToJsonString()), "application/json; charset=utf-8");
        var call = new HttpCall(url, "POST", [], body, null, $"the {platform} webhook at {url.Host}", FollowRedirects: false);

        var status = await core.ExchangeAsync(context, call, Retry(context, rateLimitOnly: true), (response, _) =>
        {
            EnsureSuccess(call, response, failOnErrorStatus: true);
            return Task.FromResult((long)(int)response.StatusCode);
        }).ConfigureAwait(false);

        Set(context, "status", status);
        return ActivityResult.Completed;
    }

    /// <summary>The colour as six upper-case hex digits (without '#'), or null.</summary>
    private static string? Color(IActivityContext context)
    {
        if (!context.HasProperty("color") || context.Evaluate("color") is not { } value)
        {
            return null;
        }

        var text = (value as string ?? throw Invalid(context, "color", "a colour as #RRGGBB")).TrimStart('#');
        return text.Length == 6 && text.All(char.IsAsciiHexDigit) ? text.ToUpperInvariant() : throw Invalid(context, "color", "a colour as #RRGGBB");
    }

    /// <summary>The JSON each platform's incoming webhook accepts.</summary>
    private static JsonObject Payload(string platform, string message, string? title, string? color) => platform switch
    {
        "Slack" when color is null => new JsonObject { ["text"] = title is null ? message : $"*{title}*\n{message}" },
        "Slack" => new JsonObject
        {
            ["text"] = title ?? message,
            ["attachments"] = new JsonArray(new JsonObject { ["color"] = "#" + color, ["title"] = title, ["text"] = message, ["fallback"] = message }),
        },
        "Discord" when title is null && color is null => new JsonObject { ["content"] = message },
        "Discord" => new JsonObject
        {
            ["embeds"] = new JsonArray(new JsonObject
            {
                ["title"] = title,
                ["description"] = message,
                ["color"] = color is null ? null : int.Parse(color, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            }),
        },
        "Teams" => new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray(new JsonObject
            {
                ["contentType"] = "application/vnd.microsoft.card.adaptive",
                ["content"] = new JsonObject
                {
                    ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                    ["type"] = "AdaptiveCard",
                    ["version"] = "1.4",
                    ["body"] = title is null
                        ? new JsonArray(TextBlock(message, bold: false))
                        : new JsonArray(TextBlock(title, bold: true), TextBlock(message, bold: false)),
                },
            }),
        },
        _ => new JsonObject { ["title"] = title, ["message"] = message, ["color"] = color is null ? null : "#" + color },
    };

    private static JsonObject TextBlock(string text, bool bold)
    {
        var block = new JsonObject { ["type"] = "TextBlock", ["text"] = text, ["wrap"] = true };
        if (bold)
        {
            block["weight"] = "Bolder";
            block["size"] = "Medium";
        }

        return block;
    }
}
