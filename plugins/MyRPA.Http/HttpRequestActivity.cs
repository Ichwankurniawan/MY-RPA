using System.Text;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;
using static MyRPA.Http.HttpCore;

namespace MyRPA.Http;

/// <summary>
/// <c>Http.Request</c>: one HTTP request (ADR-0042), repeated on connection errors, 429 and 5xx when <c>retries</c> is
/// set (ADR-0043). Secrets (token, password) come from secret properties, are sent only to the origin of the URL (never to
/// another origin after a redirect) and never appear in messages; messages name the URL without its query string.
/// </summary>
public sealed class HttpRequestActivity(HttpOptions options, HttpGateway gateway) : IActivity
{
    /// <summary>Deepest JSON nesting accepted in a response.</summary>
    public const int MaxJsonDepth = HttpCore.MaxJsonDepth;

    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Http.Request"),
        "HTTP Request",
        "HTTP",
        "Sends one HTTP request. A String body is sent as text, any other value as JSON; a JSON response becomes a workflow value. A status of 400 or above fails (HttpStatus) unless failOnErrorStatus is false. Credentials come from secret properties and are never sent to another origin on a redirect.",
        [
            new("method", ActivityPropertyKind.Text, isRequired: false, "The HTTP method.", Methods) { ValueType = ActivityValueType.String, DefaultValue = "\"GET\"" },
            Input("url", ActivityValueType.String, "The absolute http or https URL.", required: true),
            Input("body", ActivityValueType.Any, "The request body: a String is sent as text/plain, any other value as JSON."),
            .. AuthProperties(),
            .. TimingProperties("connection errors, 429 and 5xx; GET, HEAD, PUT and DELETE only, unless retryUnsafe"),
            Input("retryUnsafe", ActivityValueType.Boolean, "Also repeat POST and PATCH (only when the server ignores a repeated request, for example with an idempotency key).", defaultJson: "false"),
            Input("failOnErrorStatus", ActivityValueType.Boolean, "Fail (HttpStatus) when the status is 400 or above.", defaultJson: "true"),
            Input("parseJson", ActivityValueType.Boolean, "Turn a JSON response into a workflow value (otherwise the body is text).", defaultJson: "true"),
            Output("status", ActivityValueType.Int, "Receives the status code."),
            Output("responseHeaders", ActivityValueType.Dictionary, "Receives the response headers (lower-case names)."),
            Output("responseBody", ActivityValueType.Any, "Receives the body: a workflow value for JSON, otherwise text; null when empty."),
        ])
    { SideEffects = ActivitySideEffects.Network };

    private static string[] Methods => ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"];

    /// <inheritdoc />
    public async ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var core = new HttpCore(options, gateway);
        var method = context.GetTextOrDefault("method", "GET");
        if (!Methods.Contains(method, StringComparer.Ordinal))
        {
            throw Invalid(context, "method", string.Join(", ", Methods));
        }

        var url = core.ParseUrl(context);
        var call = new HttpCall(url, method, Headers(context), Body(context), Credential(context), Safe(url));
        var parseJson = Flag(context, "parseJson", true);
        var failOnErrorStatus = Flag(context, "failOnErrorStatus", true);
        var retry = Retry(context);

        await core.ExchangeAsync(context, call, retry, async (response, cancellationToken) =>
        {
            EnsureSuccess(call, response, failOnErrorStatus);
            var bytes = method == "HEAD" ? [] : await ReadAsync(response.Content, call, options.MaxResponseBytes, "maxResponseBytes", cancellationToken).ConfigureAwait(false);
            var value = Decode(response.Content, bytes, parseJson, call);
            Set(context, "status", (long)(int)response.StatusCode);
            Set(context, "responseHeaders", ResponseHeaders(response));
            Set(context, "responseBody", value);
            return true;
        }).ConfigureAwait(false);

        return ActivityResult.Completed;
    }

    private static Func<HttpContent>? Body(IActivityContext context)
    {
        if (!context.HasProperty("body"))
        {
            return null;
        }

        var value = context.Evaluate("body");
        return value is string text
            ? Bytes(Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8")
            : Bytes(JsonBytes(value), "application/json; charset=utf-8");
    }
}
