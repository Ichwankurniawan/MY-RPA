using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;

namespace MyRPA.Http;

/// <summary>
/// One HTTP exchange as the activities describe it. <paramref name="Content"/> builds the body for each attempt and each
/// redirect hop (a stream can be sent only once); <paramref name="Display"/> is how messages name the target (never the
/// query string, and only the host for a webhook whose path is the secret).
/// </summary>
internal sealed record HttpCall(
    Uri Url,
    string Method,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    Func<HttpContent>? Content,
    KeyValuePair<string, string>? Credential,
    string Display,
    bool FollowRedirects = true);

/// <summary>When a failed attempt is repeated (ADR-0043): connection errors, 429 and 5xx, with backoff and Retry-After.</summary>
/// <param name="Retries">How many more attempts after the first.</param>
/// <param name="DelayMs">The first delay; it doubles for each further attempt, up to <see cref="HttpCore.MaxRetryDelay"/>.</param>
/// <param name="RateLimitOnly">Only 429 is repeated (a message post must not be sent twice).</param>
/// <param name="Unsafe">POST and PATCH may be repeated too (<c>retryUnsafe</c>).</param>
internal sealed record RetryPolicy(int Retries, long DelayMs, bool RateLimitOnly = false, bool Unsafe = false);

/// <summary>The parts the HTTP activities share: properties, credentials, redirects checked hop by hop, retries, limits.</summary>
internal sealed class HttpCore(HttpOptions options, HttpGateway gateway)
{
    /// <summary>Deepest JSON nesting accepted in a response.</summary>
    public const int MaxJsonDepth = 64;

    /// <summary>The longest wait between two attempts; a longer Retry-After ends the retries.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    private static readonly string[] _retrySafeMethods = ["GET", "HEAD", "PUT", "DELETE"];

    public HttpOptions Options => options;

    /// <summary>
    /// Sends <paramref name="call"/> (following redirects, retrying under <paramref name="retry"/>) and hands the final
    /// response to <paramref name="handle"/> within the same attempt's timeout. Network failures become the HTTP error
    /// types; <see cref="ActivityFailedException"/> from <paramref name="handle"/> passes through, so a handler maps its
    /// own file errors.
    /// </summary>
    public async Task<T> ExchangeAsync<T>(IActivityContext context, HttpCall call, RetryPolicy retry, Func<HttpResponseMessage, CancellationToken, Task<T>> handle)
    {
        var timeoutSpan = Timeout(context);
        var mayRepeat = retry.RateLimitOnly || retry.Unsafe || _retrySafeMethods.Contains(call.Method, StringComparer.Ordinal);
        for (var attempt = 0; ; attempt++)
        {
            var more = mayRepeat && attempt < retry.Retries;
            TimeSpan wait;
            using (var timeout = new CancellationTokenSource(timeoutSpan, context.TimeProvider))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, timeout.Token))
            {
                HttpResponseMessage? response = null;
                try
                {
                    try
                    {
                        response = await SendAsync(call, linked.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException && more && !retry.RateLimitOnly)
                    {
                        response = null;
                    }

                    if (response is null)
                    {
                        wait = Backoff(retry, attempt);
                    }
                    else if (more && Repeatable((int)response.StatusCode, retry) && RetryAfter(context, response, Backoff(retry, attempt)) is { } delay)
                    {
                        wait = delay;
                    }
                    else
                    {
                        return await handle(response, linked.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException ex) when (!context.CancellationToken.IsCancellationRequested)
                {
                    throw new ActivityFailedException(HttpErrorTypes.Timeout, $"{call.Method} {call.Display} did not complete within the timeout.", ex);
                }
                catch (HttpRequestException ex)
                {
                    throw new ActivityFailedException(HttpErrorTypes.HttpConnection, $"{call.Method} {call.Display} failed: {Describe(ex.HttpRequestError)}.", ex);
                }
                catch (IOException ex)
                {
                    throw new ActivityFailedException(HttpErrorTypes.HttpConnection, $"{call.Method} {call.Display} failed: the connection was interrupted.", ex);
                }
                finally
                {
                    response?.Dispose();
                }
            }

            // Waiting must not outlast the run's deadline.
            if (context.Deadline is { } deadline && context.TimeProvider.GetUtcNow() + wait >= deadline)
            {
                throw new ActivityFailedException(HttpErrorTypes.Timeout, $"{call.Method} {call.Display} failed and the run's deadline leaves no time to repeat it.");
            }

            await Task.Delay(wait, context.TimeProvider, context.CancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Fails with HttpStatus for a status of 400 or above (before the body is read: an error body must not mask it).</summary>
    public static void EnsureSuccess(HttpCall call, HttpResponseMessage response, bool failOnErrorStatus)
    {
        var status = (int)response.StatusCode;
        if (status >= 400 && failOnErrorStatus)
        {
            throw new ActivityFailedException(HttpErrorTypes.HttpStatus, $"{call.Method} {call.Display} returned {status.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private static bool Repeatable(int status, RetryPolicy retry) =>
        status == 429 || (!retry.RateLimitOnly && status is 500 or 502 or 503 or 504);

    private static TimeSpan Backoff(RetryPolicy retry, int attempt)
    {
        var milliseconds = retry.DelayMs * Math.Pow(2, Math.Min(attempt, 20));
        return milliseconds >= MaxRetryDelay.TotalMilliseconds ? MaxRetryDelay : TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>
    /// The wait before the next attempt: the server's Retry-After when it gives one, else the backoff; null (no further
    /// attempt) when the server asks for longer than <see cref="MaxRetryDelay"/>.
    /// </summary>
    private static TimeSpan? RetryAfter(IActivityContext context, HttpResponseMessage response, TimeSpan backoff)
    {
        var header = response.Headers.RetryAfter;
        var asked = header?.Delta ?? (header?.Date is { } date ? date - context.TimeProvider.GetUtcNow() : null);
        if (asked is null)
        {
            return backoff;
        }

        return asked.Value > MaxRetryDelay ? null : asked.Value < TimeSpan.Zero ? TimeSpan.Zero : asked.Value;
    }

    /// <summary>Sends the request and follows redirects, checking every hop.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpCall call, CancellationToken cancellationToken)
    {
        var current = call.Url;
        var method = call.Method;
        var content = call.Content;
        var sendCredential = call.Credential is not null;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), current);
            if (content is not null)
            {
                request.Content = content();
            }

            foreach (var (name, value) in call.Headers)
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
                request.Headers.TryAddWithoutValidation(call.Credential!.Value.Key, call.Credential.Value.Value);
            }

            var response = await gateway.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location)
            {
                return response;
            }

            response.Dispose();
            if (!call.FollowRedirects)
            {
                throw new ActivityFailedException(HttpErrorTypes.RedirectNotAllowed, $"{call.Display} answered with a redirect, which is not followed here.");
            }

            if (redirects == options.MaxRedirects)
            {
                throw new ActivityFailedException(HttpErrorTypes.TooManyRedirects, $"{call.Display} redirected more than {options.MaxRedirects} times (setting maxRedirects).");
            }

            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
            {
                throw new ActivityFailedException(HttpErrorTypes.RedirectNotAllowed, $"{call.Display} redirected to a {next.Scheme} URL; only http and https are followed.");
            }

            if (current.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp)
            {
                throw new ActivityFailedException(HttpErrorTypes.RedirectNotAllowed, $"{call.Display} redirected from https to http; such a redirect is not followed.");
            }

            if (!options.Allows(next.Host))
            {
                throw new ActivityFailedException(HttpErrorTypes.HostNotAllowed, $"{call.Display} redirected to host '{next.Host}', which is not in the plugin's allowedHosts.");
            }

            // Credentials go only to the origin they were given for; once dropped they stay dropped.
            sendCredential &= Uri.Compare(current, next, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
            if (code == 303 || (code is 301 or 302 && method == "POST"))
            {
                method = method == "HEAD" ? "HEAD" : "GET";
                content = null;
            }

            current = next;
        }
    }

    // Properties shared by the activities.

    /// <summary>The URL property: absolute http/https, no user info, an allowed host. Messages never quote the URL itself.</summary>
    public Uri ParseUrl(IActivityContext context, string name = "url")
    {
        var text = context.Evaluate(name) as string ?? throw Invalid(context, name, "text");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            throw new ActivityFailedException(HttpErrorTypes.InvalidUrl, $"'{name}' of {context.Node.Type} '{context.Node.Id}' must be an absolute http or https URL.");
        }

        if (url.UserInfo.Length > 0)
        {
            throw new ActivityFailedException(HttpErrorTypes.InvalidUrl, $"'{name}' of {context.Node.Type} '{context.Node.Id}' contains a user name or password; use auth with a secret property.");
        }

        return options.Allows(url.Host)
            ? url
            : throw new ActivityFailedException(HttpErrorTypes.HostNotAllowed, $"Host '{url.Host}' is not in the plugin's allowedHosts.");
    }

    public static List<KeyValuePair<string, string>> Headers(IActivityContext context)
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

    /// <summary>The credential header for auth, or null for None.</summary>
    public static KeyValuePair<string, string>? Credential(IActivityContext context)
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

    /// <summary>The retry properties: <c>retries</c>, <c>retryDelayMs</c> and, where declared, <c>retryUnsafe</c>.</summary>
    public static RetryPolicy Retry(IActivityContext context, bool rateLimitOnly = false) =>
        new((int)Whole(context, "retries", 0, 0, 10), Whole(context, "retryDelayMs", 1000, 0, 60_000), rateLimitOnly, Flag(context, "retryUnsafe", false));

    private TimeSpan Timeout(IActivityContext context)
    {
        var timeout = TimeSpan.FromMilliseconds(Whole(context, "timeoutMs", options.DefaultTimeoutMs, 1, 3_600_000));
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

    /// <summary>Reads a response body up to <paramref name="limit"/> bytes.</summary>
    public static async Task<byte[]> ReadAsync(HttpContent content, HttpCall call, long limit, string setting, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > limit)
        {
            throw TooLarge(call, limit, setting);
        }

        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    throw TooLarge(call, limit, setting);
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    public static ActivityFailedException TooLarge(HttpCall call, long limit, string setting) =>
        new(HttpErrorTypes.ResponseTooLarge, $"The response of {call.Display} is larger than {limit} bytes (setting {setting}).");

    /// <summary>A response body as a workflow value: JSON (with parseJson) or text in the declared charset; null when empty.</summary>
    public static object? Decode(HttpContent content, byte[] bytes, bool parseJson, HttpCall call)
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
                    : throw new ActivityFailedException(HttpErrorTypes.InvalidJson, $"The JSON response of {call.Display} cannot become a workflow value: {error}");
            }
            catch (JsonException ex)
            {
                throw new ActivityFailedException(HttpErrorTypes.InvalidJson, $"The response of {call.Display} says it is JSON but is not valid JSON (nesting deeper than {MaxJsonDepth} included).", ex);
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

    public static IReadOnlyDictionary<string, object?> ResponseHeaders(HttpResponseMessage response) =>
        WorkflowValues.Dictionary(response.Headers.Concat(response.Content.Headers)
            .Select(h => new KeyValuePair<string, object?>(h.Key.ToLowerInvariant(), string.Join(", ", h.Value))));

    /// <summary>A workflow value as JSON bytes.</summary>
    public static byte[] JsonBytes(object? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WorkflowValues.WriteJson(writer, value);
        }

        return stream.ToArray();
    }

    /// <summary>Bytes as a request body of the given type (a new content per attempt).</summary>
    public static Func<HttpContent> Bytes(byte[] bytes, string mediaType) => () =>
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        return content;
    };

    /// <summary>The URL as messages show it: scheme, host, port and path — never the query string or fragment.</summary>
    public static string Safe(Uri url) => url.GetLeftPart(UriPartial.Path);

    private static string Describe(HttpRequestError error) => error switch
    {
        HttpRequestError.NameResolutionError => "the host name could not be resolved",
        HttpRequestError.ConnectionError => "the connection could not be made",
        HttpRequestError.SecureConnectionError => "the secure (TLS) connection could not be established",
        HttpRequestError.ResponseEnded => "the server closed the connection",
        HttpRequestError.ProxyTunnelError => "the proxy refused the connection",
        _ => "the request could not be completed",
    };

    public static string Text(IActivityContext context, string name) =>
        context.Evaluate(name) is string text ? text : throw Invalid(context, name, "text");

    public static bool Flag(IActivityContext context, string name, bool defaultValue) =>
        !context.HasProperty(name) ? defaultValue : context.Evaluate(name) is bool b ? b : throw Invalid(context, name, "true or false");

    public static long Whole(IActivityContext context, string name, long defaultValue, long min, long max) =>
        !context.HasProperty(name) ? defaultValue
        : context.Evaluate(name) is long value && value >= min && value <= max ? value
        : throw Invalid(context, name, $"a whole number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}");

    public static void Set(IActivityContext context, string name, object? value)
    {
        if (context.HasProperty(name))
        {
            context.SetValue(context.GetName(name), value);
        }
    }

    public static ActivityFailedException Invalid(IActivityContext context, string name, string expected) =>
        new(HttpErrorTypes.InvalidInput, $"'{name}' of {context.Node.Type} '{context.Node.Id}' must be {expected}.");

    // Descriptor helpers.

    public static ActivityPropertyDefinition Input(string name, ActivityValueType type, string description, bool required = false, string? defaultJson = null) =>
        new(name, ActivityPropertyKind.Expression, required, description) { ValueType = type, DefaultValue = defaultJson };

    public static ActivityPropertyDefinition Output(string name, ActivityValueType type, string description) =>
        new(name, ActivityPropertyKind.AssignmentTarget, isRequired: false, description) { ValueType = type };

    /// <summary>headers, auth, token, username, password, apiKeyHeader.</summary>
    public static IEnumerable<ActivityPropertyDefinition> AuthProperties() =>
    [
        Input("headers", ActivityValueType.Dictionary, "Request headers: a Dictionary of name → value (for example a variable with a default). Use auth for credentials (an Authorization header is refused)."),
        new("auth", ActivityPropertyKind.Text, isRequired: false, "Authentication.", ["None", "Bearer", "Basic", "ApiKey"]) { ValueType = ActivityValueType.String, DefaultValue = "\"None\"" },
        new("token", ActivityPropertyKind.Expression, isRequired: false, "The Bearer token or API key: an argument or variable, never a value written here.") { ValueType = ActivityValueType.String, IsSecret = true },
        Input("username", ActivityValueType.String, "The Basic user name."),
        new("password", ActivityPropertyKind.Expression, isRequired: false, "The Basic password: an argument or variable, never a value written here.") { ValueType = ActivityValueType.String, IsSecret = true },
        new("apiKeyHeader", ActivityPropertyKind.Text, isRequired: false, "The header that carries the API key (auth ApiKey).") { ValueType = ActivityValueType.String, DefaultValue = "\"X-Api-Key\"" },
    ];

    /// <summary>timeoutMs, retries, retryDelayMs.</summary>
    public static IEnumerable<ActivityPropertyDefinition> TimingProperties(string repeated) =>
    [
        Input("timeoutMs", ActivityValueType.Int, "The timeout of each attempt in milliseconds (also capped by the run's deadline).", defaultJson: "30000"),
        Input("retries", ActivityValueType.Int, $"How many times to repeat a failed attempt (0 to 10): {repeated}. The server's Retry-After is honoured (up to 60 s).", defaultJson: "0"),
        Input("retryDelayMs", ActivityValueType.Int, "The wait before the first repeat in milliseconds; it doubles for each further one (at most 60 s).", defaultJson: "1000"),
    ];
}
