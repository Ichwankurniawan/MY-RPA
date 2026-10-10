using System.Globalization;
using System.Net;
using MyRPA.Sdk.Files;
using MyRPA.Sdk.Plugins;

namespace MyRPA.Http;

/// <summary>
/// Entry point of the HTTP plugin (Phase 7, ADR-0042). Settings:
/// <list type="bullet">
/// <item><c>allowedHosts</c> — host names requests (and every redirect) may reach, separated by commas; <c>*.example.com</c>
/// matches sub-domains. Empty (the default) allows any host: set it in production (server-side request forgery).</item>
/// <item><c>maxResponseBytes</c> — the largest response body read (default 10 MB).</item>
/// <item><c>maxRedirects</c> — the most redirects followed (default 5; 0 follows none).</item>
/// <item><c>defaultTimeoutMs</c> — the timeout when a node gives none (default 30000).</item>
/// <item><c>fileRoot</c> — the only folder tree Http.Download writes to and Http.Upload reads from (ADR-0043). Not set (the
/// default), those two activities refuse every path (FileAccessDenied).</item>
/// <item><c>maxDownloadBytes</c> — the largest file Http.Download writes (default 100 MB).</item>
/// <item><c>maxUploadBytes</c> — the most file bytes one Http.Upload sends (default 100 MB).</item>
/// </list>
/// </summary>
public sealed class HttpPlugin : IPlugin
{
    private HttpOptions? _options;

    /// <inheritdoc />
    public void Initialize(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings;
        var hosts = settings.TryGetValue("allowedHosts", out var text)
            ? text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(h => h.ToLowerInvariant()).ToArray()
            : [];
        _options = new HttpOptions(
            hosts,
            Number(settings, "maxResponseBytes", 10L * 1024 * 1024, 1, int.MaxValue),
            (int)Number(settings, "maxRedirects", 5, 0, 20),
            (int)Number(settings, "defaultTimeoutMs", 30_000, 1, 3_600_000))
        {
            Files = settings.TryGetValue("fileRoot", out var root) && root.Length > 0 ? new FileRootPolicy(root) : null,
            MaxDownloadBytes = Number(settings, "maxDownloadBytes", 100L * 1024 * 1024, 1, long.MaxValue),
            MaxUploadBytes = Number(settings, "maxUploadBytes", 100L * 1024 * 1024, 1, long.MaxValue),
        };
    }

    /// <inheritdoc />
    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        registrar
            .AddInstance(_options ?? throw new InvalidOperationException("Initialize must run before Register."))
            .AddService<HttpGateway, HttpGateway>(PluginServiceLifetime.Plugin)
            .AddActivity<HttpRequestActivity>(HttpRequestActivity.Descriptor)
            .AddActivity<HttpDownloadActivity>(HttpDownloadActivity.Descriptor)
            .AddActivity<HttpUploadActivity>(HttpUploadActivity.Descriptor)
            .AddActivity<ChatPostActivity>(ChatPostActivity.Descriptor);
    }

    private static long Number(IReadOnlyDictionary<string, string> settings, string name, long defaultValue, long min, long max)
    {
        if (!settings.TryGetValue(name, out var text))
        {
            return defaultValue;
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : throw new ArgumentException($"Setting '{name}' must be a whole number from {min} to {max}, not '{text}'.", nameof(settings));
    }
}

/// <summary>Plugin settings, built once in <see cref="HttpPlugin.Initialize"/>.</summary>
/// <param name="AllowedHosts">Lower-case host names (or <c>*.domain</c>); empty allows any host.</param>
/// <param name="MaxResponseBytes">The largest response body read.</param>
/// <param name="MaxRedirects">The most redirects followed.</param>
/// <param name="DefaultTimeoutMs">The timeout when a node gives none.</param>
public sealed record HttpOptions(IReadOnlyList<string> AllowedHosts, long MaxResponseBytes, int MaxRedirects, int DefaultTimeoutMs)
{
    /// <summary>The folder tree downloads and uploads may use; null when the fileRoot setting is not given.</summary>
    public FileRootPolicy? Files { get; init; }

    /// <summary>The largest file a download writes.</summary>
    public long MaxDownloadBytes { get; init; } = 100L * 1024 * 1024;

    /// <summary>The most file bytes one upload sends.</summary>
    public long MaxUploadBytes { get; init; } = 100L * 1024 * 1024;

    /// <summary>The file policy, or a FileAccessDenied failure when the plugin has no fileRoot.</summary>
    /// <param name="activity">The activity type, for the message.</param>
    /// <returns>The policy.</returns>
    public FileRootPolicy RequireFiles(string activity) =>
        Files ?? throw new MyRPA.Workflow.Execution.ActivityFailedException(FileErrorTypes.FileAccessDenied, $"{activity} needs the HTTP plugin's fileRoot setting (the only folder it may use); it is not set.");

    /// <summary>Whether requests may reach <paramref name="host"/>.</summary>
    /// <param name="host">The URL's host.</param>
    /// <returns>True when allowed.</returns>
    public bool Allows(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (AllowedHosts.Count == 0)
        {
            return true;
        }

        var name = host.ToLowerInvariant();
        return AllowedHosts.Any(allowed => allowed.StartsWith("*.", StringComparison.Ordinal)
            ? name.EndsWith(allowed[1..], StringComparison.Ordinal)
            : name == allowed);
    }
}

/// <summary>
/// The plugin's one HTTP client (plugin lifetime, thread-safe, disposed by the host). Redirects are followed by
/// <see cref="HttpRequestActivity"/> itself, so every hop is checked; cookies are off, so runs never share them; TLS
/// certificate validation is the platform's and is never turned off.
/// </summary>
public sealed class HttpGateway : IDisposable
{
    private readonly HttpClient _client = new(
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        },
        disposeHandler: true)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>Sends a request and returns once the response headers arrived.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The response; the caller disposes it.</returns>
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}

/// <summary>The errorType values of <c>Http.Request</c> failures (ADR-0042).</summary>
public static class HttpErrorTypes
{
    /// <summary>Not an absolute http or https URL, or one with a user name or password in it.</summary>
    public const string InvalidUrl = "InvalidUrl";

    /// <summary>The host (or a redirect's host) is not in the plugin's allowedHosts.</summary>
    public const string HostNotAllowed = "HostNotAllowed";

    /// <summary>The server answered 400 or above and failOnErrorStatus is true.</summary>
    public const string HttpStatus = "HttpStatus";

    /// <summary>The server could not be reached, or the connection failed (DNS, refused, TLS, reset).</summary>
    public const string HttpConnection = "HttpConnection";

    /// <summary>No complete response within the timeout.</summary>
    public const string Timeout = "Timeout";

    /// <summary>The response body is larger than maxResponseBytes.</summary>
    public const string ResponseTooLarge = "ResponseTooLarge";

    /// <summary>More redirects than maxRedirects.</summary>
    public const string TooManyRedirects = "TooManyRedirects";

    /// <summary>A redirect to a scheme other than http/https, or from https to http.</summary>
    public const string RedirectNotAllowed = "RedirectNotAllowed";

    /// <summary>A JSON response that is not valid JSON (with parseJson).</summary>
    public const string InvalidJson = "InvalidJson";

    /// <summary>A property has a value of the wrong kind.</summary>
    public const string InvalidInput = "InvalidInput";
}
