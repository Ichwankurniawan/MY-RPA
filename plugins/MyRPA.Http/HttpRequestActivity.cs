using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;

namespace MyRPA.Http;

/// <summary>
/// <c>Http.Request</c>: one HTTP request (ADR-0042). Secrets (token, password) come from secret properties, are sent only
/// to the origin of the URL (never to another origin after a redirect) and never appear in messages; messages name the
/// URL without its query string.
/// </summary>
public sealed class HttpRequestActivity(HttpOptions options, HttpGateway gateway) : IActivity
{
    /// <summary>Deepest JSON nesting accepted in a response.</summary>
    public const int MaxJsonDepth = 64;

    /// <summary>Descriptor.</summary>
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Http.Request"),
        "HTTP Request",
        "HTTP",
        "Sends one HTTP request. A String body is sent as text, any other value as JSON; a JSON response becomes a workflow value. A status of 400 or above fails (HttpStatus) unless failOnErrorStatus is false. Credentials come from secret properties and are never sent to another origin on a redirect.",
        [
            new("method", ActivityPropertyKind.Text, isRequired: false, "The HTTP method.", Methods) { ValueType = ActivityValueType.String, DefaultValue = "\"GET\"" },
            Input("url", ActivityValueType.String, "The absolute http or https URL.", required: true),
            Input("headers", ActivityValueType.Dictionary, "Request headers: a Dictionary of name → value (for example a variable with a default). Use auth for credentials (an Authorization header is refused)."),
            Input("body", ActivityValueType.Any, "The request body: a String is sent as text/plain, any other value as JSON."),
            new("auth", ActivityPropertyKind.Text, isRequired: false, "Authentication.", ["None", "Bearer", "Basic", "ApiKey"]) { ValueType = ActivityValueType.String, DefaultValue = "\"None\"" },
            new("token", ActivityPropertyKind.Expression, isRequired: false, "The Bearer token or API key: an argument or variable, never a value written here.") { ValueType = ActivityValueType.String, IsSecret = true },
            Input("username", ActivityValueType.String, "The Basic user name."),
            new("password", ActivityPropertyKind.Expression, isRequired: false, "The Basic password: an argument or variable, never a value written here.") { ValueType = ActivityValueType.String, IsSecret = true },
            new("apiKeyHeader", ActivityPropertyKind.Text, isRequired: false, "The header that carries the API key (auth ApiKey).") { ValueType = ActivityValueType.String, DefaultValue = "\"X-Api-Key\"" },
            Input("timeoutMs", ActivityValueType.Int, "The timeout in milliseconds (also capped by the run's deadline).", defaultJson: "30000"),
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
        var method = context.GetTextOrDefault("method", "GET");
        if (!Methods.Contains(method, StringComparer.Ordinal))
        {
            throw Invalid(context, "method", string.Join(", ", Methods));
        }

        var url = ParseUrl(context);
        var headers = Headers(context);
        var (body, bodyType) = Body(context);
        var credential = Credential(context);
        var parseJson = Flag(context, "parseJson", true);
        var failOnErrorStatus = Flag(context, "failOnErrorStatus", true);

        using var timeout = new CancellationTokenSource(Timeout(context), context.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, timeout.Token);
        try
        {
            using var response = await SendAsync(url, method, headers, body, bodyType, credential, linked.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status >= 400 && failOnErrorStatus)
            {
                // Before decoding or assigning: an error body (often another shape) must not mask the status failure.
                throw new ActivityFailedException(HttpErrorTypes.HttpStatus, $"{method} {Safe(url)} returned {status.ToString(CultureInfo.InvariantCulture)}.");
            }

            var bytes = method == "HEAD" ? [] : await ReadAsync(response.Content, url, linked.Token).ConfigureAwait(false);
            var value = Decode(response.Content, bytes, parseJson, url);
            Set(context, "status", (long)status);
            Set(context, "responseHeaders", ResponseHeaders(response));
            Set(context, "responseBody", value);
        }
        catch (OperationCanceledException ex) when (!context.CancellationToken.IsCancellationRequested)
        {
            throw new ActivityFailedException(HttpErrorTypes.Timeout, $"{method} {Safe(url)} did not complete within the timeout.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ActivityFailedException(HttpErrorTypes.HttpConnection, $"{method} {Safe(url)} failed: {Describe(ex.HttpRequestError)}.", ex);
        }
        catch (IOException ex)
        {
            throw new ActivityFailedException(HttpErrorTypes.HttpConnection, $"{method} {Safe(url)} failed: the connection was interrupted.", ex);
        }

        return ActivityResult.Completed;
    }

    private static ActivityPropertyDefinition Input(string name, ActivityValueType type, string description, bool required = false, string? defaultJson = null) =>
        new(name, ActivityPropertyKind.Expression, required, description) { ValueType = type, DefaultValue = defaultJson };

    private static ActivityPropertyDefinition Output(string name, ActivityValueType type, string description) =>
        new(name, ActivityPropertyKind.AssignmentTarget, isRequired: false, description) { ValueType = type };

    /// <summary>Sends the request and follows redirects, checking every hop.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        Uri url, string method, List<KeyValuePair<string, string>> headers, byte[]? body, string? bodyType, KeyValuePair<string, string>? credential, CancellationToken cancellationToken)
    {
        var current = url;
        var sendCredential = credential is not null;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), current);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(bodyType!);
            }

            foreach (var (name, value) in headers)
            {
                if (!name.StartsWith("content-", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
                else if (request.Content is not null)
                {
                    request.Content.Headers.Remove(name);
                    request.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }

            if (sendCredential)
            {
                request.Headers.TryAddWithoutValidation(credential!.Value.Key, credential.Value.Value);
            }

            var response = await gateway.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location)
            {
                return response;
            }

            response.Dispose();
            if (redirects == options.MaxRedirects)
            {
                throw new ActivityFailedException(HttpErrorTypes.TooManyRedirects, $"{Safe(url)} redirected more than {options.MaxRedirects} times (setting maxRedirects).");
            }

            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
            {
                throw new ActivityFailedException(HttpErrorTypes.RedirectNotAllowed, $"{Safe(url)} redirected to a {next.Scheme} URL; only http and https are followed.");
            }

            if (current.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp)
            {
                throw new ActivityFailedException(HttpErrorTypes.RedirectNotAllowed, $"{Safe(url)} redirected from https to http; such a redirect is not followed.");
            }

            if (!options.Allows(next.Host))
            {
                throw new ActivityFailedException(HttpErrorTypes.HostNotAllowed, $"{Safe(url)} redirected to host '{next.Host}', which is not in the plugin's allowedHosts.");
            }

            // Credentials go only to the origin they were given for; once dropped they stay dropped.
            sendCredential &= Uri.Compare(current, next, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
            if (code == 303 || (code is 301 or 302 && method == "POST"))
            {
                method = method == "HEAD" ? "HEAD" : "GET";
                body = null;
            }

            current = next;
        }
    }

    private Uri ParseUrl(IActivityContext context)
    {
        var text = Text(context, "url");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            throw new ActivityFailedException(HttpErrorTypes.InvalidUrl, $"'url' of {context.Node.Type} '{context.Node.Id}' must be an absolute http or https URL.");
        }

        if (url.UserInfo.Length > 0)
        {
            throw new ActivityFailedException(HttpErrorTypes.InvalidUrl, $"'url' of {context.Node.Type} '{context.Node.Id}' contains a user name or password; use auth with a secret property.");
        }

        return options.Allows(url.Host)
            ? url
            : throw new ActivityFailedException(HttpErrorTypes.HostNotAllowed, $"Host '{url.Host}' is not in the plugin's allowedHosts.");
    }

    private static List<KeyValuePair<string, string>> Headers(IActivityContext context)
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (!context.HasProperty("headers"))
        {
            return headers;
        }

        var map = context.Evaluate("headers") as IReadOnlyDictionary<string, object?> ?? throw Invalid(context, "headers", "a Dictionary of header names and values");
        foreach (var (name, raw) in map)
        {
            if (raw is null)
            {
                continue;
            }

            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
            {
                throw new ActivityFailedException(HttpErrorTypes.InvalidInput, $"Header '{name}' of {context.Node.Type} '{context.Node.Id}' is not allowed: use auth with the secret token or password.");
            }

            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) || name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                throw new ActivityFailedException(HttpErrorTypes.InvalidInput, $"Header '{name}' of {context.Node.Type} '{context.Node.Id}' is set by the HTTP client and cannot be given.");
            }

            var value = raw as string ?? WorkflowValues.ToDisplayString(raw);
            if (name.Length == 0 || !name.All(c => c > ' ' && c < 127 && c != ':') || value.Contains('\r') || value.Contains('\n'))
            {
                throw new ActivityFailedException(HttpErrorTypes.InvalidInput, $"Header '{name}' of {context.Node.Type} '{context.Node.Id}' has an invalid name or a line break in its value.");
            }

            headers.Add(new(name, value));
        }

        return headers;
    }

    private static (byte[]? Body, string? Type) Body(IActivityContext context)
    {
        if (!context.HasProperty("body"))
        {
            return (null, null);
        }

        var value = context.Evaluate("body");
        if (value is string text)
        {
            return (Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WorkflowValues.WriteJson(writer, value);
        }

        return (stream.ToArray(), "application/json; charset=utf-8");
    }

    /// <summary>The credential header for auth, or null for None.</summary>
    private static KeyValuePair<string, string>? Credential(IActivityContext context)
    {
        string Secret(string name) =>
            context.HasProperty(name) && context.Evaluate(name) is string { Length: > 0 } value
                ? value
                : throw new ActivityFailedException(HttpErrorTypes.InvalidInput, $"'{name}' of {context.Node.Type} '{context.Node.Id}' must be a non-empty text for this auth.");

        switch (context.GetTextOrDefault("auth", "None"))
        {
            case "Bearer":
                return new("Authorization", "Bearer " + Secret("token"));
            case "Basic":
                var user = Text(context, "username");
                if (user.Contains(':'))
                {
                    throw new ActivityFailedException(HttpErrorTypes.InvalidInput, $"'username' of {context.Node.Type} '{context.Node.Id}' cannot contain ':' for Basic authentication.");
                }

                return new("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + Secret("password"))));
            case "ApiKey":
                return new(context.GetTextOrDefault("apiKeyHeader", "X-Api-Key"), Secret("token"));
            default:
                return null;
        }
    }

    private TimeSpan Timeout(IActivityContext context)
    {
        var milliseconds = !context.HasProperty("timeoutMs")
            ? options.DefaultTimeoutMs
            : context.Evaluate("timeoutMs") is long value && value is > 0 and <= 3_600_000 ? value : throw Invalid(context, "timeoutMs", "a whole number of milliseconds from 1 to 3600000");
        var timeout = TimeSpan.FromMilliseconds(milliseconds);
        if (context.Deadline is { } deadline)
        {
            var remaining = deadline - context.TimeProvider.GetUtcNow();
            if (remaining < timeout)
            {
                timeout = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
            }
        }

        return timeout;
    }

    private async Task<byte[]> ReadAsync(HttpContent content, Uri url, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > options.MaxResponseBytes)
        {
            throw TooLarge(url);
        }

        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > options.MaxResponseBytes)
                {
                    throw TooLarge(url);
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    private ActivityFailedException TooLarge(Uri url) =>
        new(HttpErrorTypes.ResponseTooLarge, $"The response of {Safe(url)} is larger than {options.MaxResponseBytes} bytes (setting maxResponseBytes).");

    private static object? Decode(HttpContent content, byte[] bytes, bool parseJson, Uri url)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        var type = content.Headers.ContentType;
        var mediaType = type?.MediaType ?? string.Empty;
        if (parseJson && (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
                return WorkflowValues.TryFromJsonUntyped(document.RootElement, out var value, out var error)
                    ? value
                    : throw new ActivityFailedException(HttpErrorTypes.InvalidJson, $"The JSON response of {Safe(url)} cannot become a workflow value: {error}");
            }
            catch (JsonException ex)
            {
                throw new ActivityFailedException(HttpErrorTypes.InvalidJson, $"The response of {Safe(url)} says it is JSON but is not valid JSON (nesting deeper than {MaxJsonDepth} included).", ex);
            }
        }

        var encoding = Encoding.UTF8;
        if (type?.CharSet is { Length: > 0 } charset)
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                // An unknown charset: read as UTF-8.
            }
        }

        return encoding.GetString(bytes);
    }

    private static IReadOnlyDictionary<string, object?> ResponseHeaders(HttpResponseMessage response) =>
        WorkflowValues.Dictionary(response.Headers.Concat(response.Content.Headers)
            .Select(h => new KeyValuePair<string, object?>(h.Key.ToLowerInvariant(), string.Join(", ", h.Value))));

    /// <summary>The URL as messages show it: scheme, host, port and path — never the query string or fragment.</summary>
    private static string Safe(Uri url) => url.GetLeftPart(UriPartial.Path);

    private static string Describe(HttpRequestError error) => error switch
    {
        HttpRequestError.NameResolutionError => "the host name could not be resolved",
        HttpRequestError.ConnectionError => "the connection could not be made",
        HttpRequestError.SecureConnectionError => "the secure (TLS) connection could not be established",
        HttpRequestError.ResponseEnded => "the server closed the connection",
        HttpRequestError.ProxyTunnelError => "the proxy refused the connection",
        _ => "the request could not be completed",
    };

    private static string Text(IActivityContext context, string name) =>
        context.Evaluate(name) is string text ? text : throw Invalid(context, name, "text");

    private static bool Flag(IActivityContext context, string name, bool defaultValue) =>
        !context.HasProperty(name) ? defaultValue : context.Evaluate(name) is bool b ? b : throw Invalid(context, name, "true or false");

    private static void Set(IActivityContext context, string name, object? value)
    {
        if (context.HasProperty(name))
        {
            context.SetValue(context.GetName(name), value);
        }
    }

    private static ActivityFailedException Invalid(IActivityContext context, string name, string expected) =>
        new(HttpErrorTypes.InvalidInput, $"'{name}' of {context.Node.Type} '{context.Node.Id}' must be {expected}.");
}
