using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace MyRPA.Server;

/// <summary>
/// Local-mode sessions (ADR-0025): a one-time start token, printed at startup, is exchanged once for a random session
/// cookie. The server is single-user; sessions only prove that the browser was opened from the printed link.
/// </summary>
internal sealed class LocalSessions(ServerOptions options, TimeProvider time)
{
    public const string CookieName = "myrpa_session";

    private readonly ConcurrentDictionary<string, byte> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private string? _startToken = NewSecret();
    private readonly DateTimeOffset _startTokenExpires = time.GetUtcNow() + options.StartTokenLifetime;

    /// <summary>The one-time start token (null once used).</summary>
    public string? StartToken
    {
        get
        {
            lock (_gate)
            {
                return _startToken;
            }
        }
    }

    /// <summary>Exchanges the start token for a session id; the token can be used only once and expires.</summary>
    public string? Redeem(string? token)
    {
        lock (_gate)
        {
            if (_startToken is null || token is null || time.GetUtcNow() > _startTokenExpires
                || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(token), System.Text.Encoding.ASCII.GetBytes(_startToken)))
            {
                return null;
            }

            _startToken = null;
        }

        var session = NewSecret();
        _sessions[session] = 0;
        return session;
    }

    public bool IsValid(string? session) => session is not null && _sessions.ContainsKey(session);

    public static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
}

/// <summary>The request guard of local mode (ADR-0025).</summary>
internal static class LocalSecurity
{
    public const string AntiForgeryHeader = "X-MyRPA-Request";

    private static readonly string[] _loopbackHosts = ["127.0.0.1", "localhost", "[::1]"];

    public static IApplicationBuilder UseLocalSecurity(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.ContentSecurityPolicy = "default-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.XFrameOptions = "DENY";

        // DNS rebinding: only loopback host names, on the port this connection arrived at.
        if (!IsLoopbackHost(context))
        {
            await Reject(context, StatusCodes.Status400BadRequest, "Invalid host.").ConfigureAwait(false);
            return;
        }

        var isApi = request.Path.StartsWithSegments("/api", StringComparison.Ordinal);
        if (isApi)
        {
            response.Headers.CacheControl = "no-store";
            var sessions = context.RequestServices.GetRequiredService<LocalSessions>();
            if (!sessions.IsValid(request.Cookies[LocalSessions.CookieName]))
            {
                await Reject(context, StatusCodes.Status401Unauthorized, "Open the server with the start link it printed.").ConfigureAwait(false);
                return;
            }
        }

        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            // Cross-site requests: they carry a foreign Origin, and cannot add a custom header without a CORS preflight,
            // which this server never answers.
            var origin = $"{request.Scheme}://{request.Host}";
            if (!string.Equals(request.Headers.Origin, origin, StringComparison.Ordinal) || request.Headers[AntiForgeryHeader] != "1")
            {
                await Reject(context, StatusCodes.Status403Forbidden, "Cross-origin or unmarked request refused.").ConfigureAwait(false);
                return;
            }

            if ((request.ContentLength ?? 0) > 0 && request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
            {
                await Reject(context, StatusCodes.Status415UnsupportedMediaType, "Request bodies must be application/json.").ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    });

    private static bool IsLoopbackHost(HttpContext context)
    {
        var host = context.Request.Host;
        return host.HasValue
            && host.Port == context.Connection.LocalPort
            && _loopbackHosts.Contains(host.Host, StringComparer.OrdinalIgnoreCase);
    }

    private static Task Reject(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = message });
    }
}
