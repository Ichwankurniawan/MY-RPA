using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using MyRPA.Contracts.Orchestration;

namespace MyRPA.Runner;

/// <summary>The Server refused the Runner's token (revoked or wrong): the Runner stops.</summary>
public sealed class RunnerAuthenticationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RunnerAuthenticationException()
        : base("The Server refused this Runner's token (it is wrong or was revoked).")
    {
    }

    /// <summary>Creates the exception.</summary>
    public RunnerAuthenticationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public RunnerAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A Runner request failed (after its retries, for transient failures).</summary>
public sealed class RunnerProtocolException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RunnerProtocolException(string message, HttpStatusCode? status, Exception? innerException = null)
        : base(message, innerException) => Status = status;

    /// <summary>Creates the exception.</summary>
    public RunnerProtocolException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public RunnerProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public RunnerProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The HTTP status, when the Server answered.</summary>
    public HttpStatusCode? Status { get; }
}

/// <summary>
/// The Runner's connection to the Server (ADR-0049): every call is outbound HTTPS with the Runner's bearer token, the
/// protocol version and a correlation id. Transient failures (network errors, timeouts, 5xx, 408, 429) are retried with
/// exponential backoff up to a bound; everything else is reported to the caller. The only type of the Runner allowed to
/// make HTTP calls (ArchitectureRules.BannedApiExemptions).
/// </summary>
public sealed partial class RunnerConnection : IDisposable
{
    private readonly HttpClient _client;
    private readonly RunnerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RunnerConnection> _logger;
    private long _requests;

    /// <summary>Creates the connection.</summary>
    public RunnerConnection(RunnerOptions options, TimeProvider time, ILogger<RunnerConnection> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
#pragma warning disable CA2000 // The handler is owned (and disposed) by the client.
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
#pragma warning restore CA2000
        if (options.ServerCertificateSha256 is { } pin)
        {
            // A pinned (typically self-signed) Server certificate: trust exactly that certificate, nothing else.
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null && string.Equals(Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)), pin, StringComparison.Ordinal);
        }

        _client = new HttpClient(handler) { BaseAddress = new Uri(options.ServerUrl, RunnerProtocol.PathPrefix + "/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Registers (or re-registers) this Runner.</summary>
    public Task<RunnerRegisterResponse> RegisterAsync(RunnerRegisterRequest request, CancellationToken cancellationToken) =>
        PostAsync("register", request, RunnerProtocolJsonContext.Default.RunnerRegisterRequest, RunnerProtocolJsonContext.Default.RunnerRegisterResponse, 3, TimeSpan.FromSeconds(30), cancellationToken);

    /// <summary>Sends a heartbeat.</summary>
    public Task<RunnerHeartbeatResponse> HeartbeatAsync(RunnerHeartbeatRequest request, CancellationToken cancellationToken) =>
        PostAsync("heartbeat", request, RunnerProtocolJsonContext.Default.RunnerHeartbeatRequest, RunnerProtocolJsonContext.Default.RunnerHeartbeatResponse, 2, TimeSpan.FromSeconds(30), cancellationToken);

    /// <summary>Claims a job, waiting up to <paramref name="wait"/>; null when none was available.</summary>
    public async Task<JobAssignment?> ClaimAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => Json(HttpMethod.Post, "claim", new RunnerClaimRequest { WaitMs = (int)wait.TotalMilliseconds }, RunnerProtocolJsonContext.Default.RunnerClaimRequest),
            1,
            wait + TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, RunnerProtocolJsonContext.Default.JobAssignment, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Downloads a job's package (served only under the job's current lease).</summary>
    public async Task<byte[]> DownloadPackageAsync(string jobId, string leaseId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"jobs/{Uri.EscapeDataString(jobId)}/package?lease={Uri.EscapeDataString(leaseId)}"),
            4,
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tells the Server the job starts.</summary>
    public Task<JobLeaseResponse> StartAsync(string jobId, JobStartRequest request, CancellationToken cancellationToken) =>
        LeaseAsync($"jobs/{Uri.EscapeDataString(jobId)}/start", request, RunnerProtocolJsonContext.Default.JobStartRequest, 4, cancellationToken);

    /// <summary>Sends a batch of events.</summary>
    public Task<JobLeaseResponse> SendEventsAsync(string jobId, JobEventsRequest request, CancellationToken cancellationToken) =>
        LeaseAsync($"jobs/{Uri.EscapeDataString(jobId)}/events", request, RunnerProtocolJsonContext.Default.JobEventsRequest, 3, cancellationToken);

    /// <summary>Reports the outcome (retried for a long time: the outcome must not be lost).</summary>
    public Task<JobLeaseResponse> CompleteAsync(string jobId, JobCompleteRequest request, CancellationToken cancellationToken) =>
        LeaseAsync($"jobs/{Uri.EscapeDataString(jobId)}/complete", request, RunnerProtocolJsonContext.Default.JobCompleteRequest, 20, cancellationToken);

    /// <summary>Gives back a job that was not started.</summary>
    public Task<JobLeaseResponse> ReleaseAsync(string jobId, JobReleaseRequest request, CancellationToken cancellationToken) =>
        LeaseAsync($"jobs/{Uri.EscapeDataString(jobId)}/release", request, RunnerProtocolJsonContext.Default.JobReleaseRequest, 3, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    private async Task<JobLeaseResponse> LeaseAsync<TRequest>(string path, TRequest body, JsonTypeInfo<TRequest> type, int attempts, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => Json(HttpMethod.Post, path, body, type), attempts, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict)
        {
            // 409 carries the same answer with LeaseValid false (stale or lost lease).
            return await ReadAsync(response, RunnerProtocolJsonContext.Default.JobLeaseResponse, cancellationToken).ConfigureAwait(false);
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        throw new RunnerProtocolException($"Unexpected answer {(int)response.StatusCode} from the Server.", response.StatusCode);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest body, JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, int attempts, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => Json(HttpMethod.Post, path, body, requestType), attempts, timeout, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, responseType, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, int attempts, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(500);
        for (var attempt = 1; ; attempt++)
        {
            using var request = build();
            var correlation = $"{Environment.MachineName}-{Interlocked.Increment(ref _requests).ToString(CultureInfo.InvariantCulture)}";
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
            request.Headers.Add(RunnerProtocol.VersionHeader, RunnerProtocol.Version.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add(RunnerProtocol.CorrelationHeader, correlation);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            string failure;
            try
            {
                var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    response.Dispose();
                    throw new RunnerAuthenticationException();
                }

                if (!IsTransient(response.StatusCode))
                {
                    return response;
                }

                failure = $"HTTP {(int)response.StatusCode}";
                response.Dispose();
            }
            catch (HttpRequestException ex)
            {
                failure = ex.Message;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failure = "timed out";
            }

            if (attempt >= attempts)
            {
                throw new RunnerProtocolException($"The Server could not be reached ({failure}).");
            }

            LogRetry(_logger, request.RequestUri?.ToString() ?? "?", correlation, failure, attempt);
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 30_000));
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    private static HttpRequestMessage Json<T>(HttpMethod method, string path, T body, JsonTypeInfo<T> type) =>
        new(method, path) { Content = JsonContent.Create(body, type, new MediaTypeHeaderValue("application/json")) };

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonSerializer.DeserializeAsync(stream, type, cancellationToken).ConfigureAwait(false)
                ?? throw new RunnerProtocolException("The Server sent an empty answer.");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string message;
        try
        {
            var error = await ReadAsync(response, RunnerProtocolJsonContext.Default.RunnerErrorResponse, cancellationToken).ConfigureAwait(false);
            message = error.Error;
        }
        catch (JsonException)
        {
            message = $"HTTP {(int)response.StatusCode}";
        }

        throw new RunnerProtocolException(message, response.StatusCode);
    }

    [LoggerMessage(EventId = 6000, Level = LogLevel.Warning, Message = "Request {Path} ({Correlation}) failed: {Failure}; retry {Attempt}")]
    private static partial void LogRetry(ILogger logger, string path, string correlation, string failure, int attempt);
}
