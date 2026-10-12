using MyRPA.Contracts.Orchestration;
using MyRPA.Orchestration;

namespace MyRPA.Server;

/// <summary>The ports the Server listens on, known once it has started.</summary>
internal sealed class ServerListeners
{
    private int _runnerPort = -1;

    /// <summary>The Runner listener's port (-1: none, or not started yet).</summary>
    public int RunnerPort
    {
        get => Volatile.Read(ref _runnerPort);
        set => Volatile.Write(ref _runnerPort, value);
    }

    /// <summary>The Runner listener's address as Runners use it.</summary>
    public Uri? RunnerUri { get; set; }

    /// <summary>When the Server started.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Whether a connection arrived on the Runner listener.</summary>
    public bool IsRunnerListener(int localPort) => localPort == RunnerPort;
}

/// <summary>
/// Who is calling (ADR-0051). On the loopback Studio listener a browser session (the start link) is the local owner, an
/// administrator; a bearer access token stands for its own role. A Runner authenticates with its own token and may only
/// use the Runner protocol. Tokens are compared by hash and never logged.
/// </summary>
internal static class RequestIdentity
{
    private const string ActorKey = "myrpa.actor";
    private const string RunnerKey = "myrpa.runner";

    /// <summary>The authenticated actor of a management request.</summary>
    public static Actor Actor(HttpContext context) =>
        context.Items[ActorKey] as Actor ?? throw new InvalidOperationException("The request has no authenticated actor.");

    /// <summary>The authenticated Runner of a Runner protocol request.</summary>
    public static RunnerRecord Runner(HttpContext context) =>
        context.Items[RunnerKey] as RunnerRecord ?? throw new InvalidOperationException("The request has no authenticated Runner.");

    /// <summary>Marks a browser-session request as the local owner.</summary>
    public static void SetLocalOwner(HttpContext context) => context.Items[ActorKey] = Orchestration.Actor.LocalOwner;

    /// <summary>The bearer token of a request, if any.</summary>
    public static string? Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : null;
    }

    /// <summary>Authenticates a management request by access token; answers 401 and returns false otherwise.</summary>
    public static async Task<bool> AuthenticateAccessTokenAsync(HttpContext context)
    {
        if (context.RequestServices.GetService<Orchestrator>() is not { } orchestrator)
        {
            await Reject(context, StatusCodes.Status404NotFound, "The orchestrator is not enabled on this Server (start it with --data).").ConfigureAwait(false);
            return false;
        }

        var actor = await orchestrator.AuthenticateAccessTokenAsync(Bearer(context), context.RequestAborted).ConfigureAwait(false);
        if (actor is null)
        {
            await Reject(context, StatusCodes.Status401Unauthorized, "A valid access token is required.").ConfigureAwait(false);
            return false;
        }

        context.Items[ActorKey] = actor;
        return true;
    }

    /// <summary>Authenticates a Runner protocol request; answers 401 (or 400 for another protocol version) otherwise.</summary>
    public static async Task<bool> AuthenticateRunnerAsync(HttpContext context)
    {
        if (context.RequestServices.GetService<Orchestrator>() is not { } orchestrator)
        {
            await Reject(context, StatusCodes.Status404NotFound, "The orchestrator is not enabled on this Server (start it with --data).").ConfigureAwait(false);
            return false;
        }

        if (context.Request.Headers[RunnerProtocol.VersionHeader].ToString() != RunnerProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            await Reject(context, StatusCodes.Status400BadRequest, $"This Server speaks Runner protocol {RunnerProtocol.Version}; update the Runner or the Server.").ConfigureAwait(false);
            return false;
        }

        var runner = await orchestrator.AuthenticateRunnerAsync(Bearer(context), context.RequestAborted).ConfigureAwait(false);
        if (runner is null)
        {
            await Reject(context, StatusCodes.Status401Unauthorized, "The Runner token is invalid or revoked.").ConfigureAwait(false);
            return false;
        }

        context.Items[RunnerKey] = runner;
        return true;
    }

    /// <summary>
    /// The Runner listener (ADR-0051): no browser UI, no cookies. Only the Runner protocol (Runner tokens) and the
    /// management API (access tokens) are served; everything else is not found.
    /// </summary>
    public static async Task RunnerListenerAsync(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path;
        context.Response.Headers.CacheControl = "no-store";
        if (path.StartsWithSegments(RunnerProtocol.PathPrefix, StringComparison.Ordinal))
        {
            if (await AuthenticateRunnerAsync(context).ConfigureAwait(false))
            {
                await next(context).ConfigureAwait(false);
            }

            return;
        }

        if (path.StartsWithSegments("/api/orchestrator", StringComparison.Ordinal))
        {
            if (!HttpMethods.IsGet(context.Request.Method) && (context.Request.ContentLength ?? 0) > 0
                && context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
            {
                await Reject(context, StatusCodes.Status415UnsupportedMediaType, "Request bodies must be application/json.").ConfigureAwait(false);
                return;
            }

            if (await AuthenticateAccessTokenAsync(context).ConfigureAwait(false))
            {
                await next(context).ConfigureAwait(false);
            }

            return;
        }

        await Reject(context, StatusCodes.Status404NotFound, "Not found.").ConfigureAwait(false);
    }

    private static Task Reject(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = message });
    }
}
