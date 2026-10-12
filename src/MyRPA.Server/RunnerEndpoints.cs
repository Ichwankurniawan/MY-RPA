using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MyRPA.Contracts.Orchestration;
using MyRPA.Orchestration;

namespace MyRPA.Server;

/// <summary>
/// The Runner protocol endpoints (ADR-0049): <c>/runner/v1/*</c>, authenticated by Runner token (the middleware sets the
/// Runner). A Runner can only act on jobs it holds under their current lease; anything else is answered as an invalid
/// lease and changes nothing.
/// </summary>
internal static class RunnerEndpoints
{
    /// <summary>Header carrying the package hash on a package download.</summary>
    public const string PackageHashHeader = "X-MyRPA-Package-Sha256";

    public static void Map(WebApplication app)
    {
        var runner = app.MapGroup(RunnerProtocol.PathPrefix);
        runner.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context).ConfigureAwait(false);
            }
            catch (OrchestrationException ex)
            {
                return Error(OrchestratorEndpoints.StatusOf(ex.Failure), ex.Message);
            }
            catch (JsonException)
            {
                return Error(StatusCodes.Status400BadRequest, "The request body is not valid JSON for this message.");
            }
        });

        runner.MapPost("/register", async (HttpContext context, Orchestrator orchestrator) =>
        {
            var request = await ReadAsync(context, RunnerProtocolJsonContext.Default.RunnerRegisterRequest).ConfigureAwait(false);
            if (request.ProtocolVersion != RunnerProtocol.Version)
            {
                return Error(StatusCodes.Status400BadRequest, $"This Server speaks Runner protocol {RunnerProtocol.Version}.");
            }

            var response = await orchestrator.RegisterAsync(RequestIdentity.Runner(context), request, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(response, RunnerProtocolJsonContext.Default.RunnerRegisterResponse);
        });

        runner.MapPost("/heartbeat", async (HttpContext context, Orchestrator orchestrator) =>
        {
            var request = await ReadAsync(context, RunnerProtocolJsonContext.Default.RunnerHeartbeatRequest).ConfigureAwait(false);
            var response = await orchestrator.HeartbeatAsync(RequestIdentity.Runner(context), request, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(response, RunnerProtocolJsonContext.Default.RunnerHeartbeatResponse);
        });

        runner.MapPost("/claim", async (HttpContext context, Orchestrator orchestrator) =>
        {
            var request = await ReadAsync(context, RunnerProtocolJsonContext.Default.RunnerClaimRequest).ConfigureAwait(false);
            var wait = TimeSpan.FromMilliseconds(Math.Clamp(request.WaitMs, 0, RunnerProtocol.MaxClaimWaitMs));
            try
            {
                var assignment = await orchestrator.ClaimAsync(RequestIdentity.Runner(context), wait, context.RequestAborted).ConfigureAwait(false);
                return assignment is null ? Results.NoContent() : Results.Json(assignment, RunnerProtocolJsonContext.Default.JobAssignment);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // The Runner went away while waiting; nothing was claimed for it.
                return Results.Empty;
            }
        });

        runner.MapGet("/jobs/{jobId}/package", async (string jobId, string? lease, HttpContext context, Orchestrator orchestrator) =>
        {
            // Packages are served only to the Runner holding the job's current lease.
            var job = await orchestrator.FindLeasedJobAsync(RequestIdentity.Runner(context), jobId, lease, context.RequestAborted).ConfigureAwait(false);
            if (job is null)
            {
                return Error(StatusCodes.Status404NotFound, "No such job is assigned to this Runner.");
            }

            var content = await orchestrator.Store.ReadPackageContentAsync(job.ProcessId, job.Version, context.RequestAborted).ConfigureAwait(false);
            if (content is null)
            {
                return Error(StatusCodes.Status404NotFound, "The package is missing.");
            }

            context.Response.Headers[PackageHashHeader] = job.PackageSha256;
            return Results.Bytes(content, "application/json");
        });

        runner.MapPost("/jobs/{jobId}/start", async (string jobId, HttpContext context, Orchestrator orchestrator) =>
        {
            var request = await ReadAsync(context, RunnerProtocolJsonContext.Default.JobStartRequest).ConfigureAwait(false);
            return Lease(await orchestrator.StartAsync(RequestIdentity.Runner(context), jobId, request, context.RequestAborted).ConfigureAwait(false));
        });

        runner.MapPost("/jobs/{jobId}/events", async (string jobId, HttpContext context, Orchestrator orchestrator) =>
        {
            var request = await ReadAsync(context, RunnerProtocolJsonContext.Default.JobEventsRequest).ConfigureAwait(false);
            return Lease(await orchestrator.AppendEventsAsync(RequestIdentity.Runner(context), jobId, request, context.RequestAborted).ConfigureAwait(false));
        });

        runner.MapPost("/jobs/{jobId}/complete", async (string jobId, HttpContext context, Orchestrator orchestrator) =>
        {
            var request = await ReadAsync(context, RunnerProtocolJsonContext.Default.JobCompleteRequest).ConfigureAwait(false);
            return Lease(await orchestrator.CompleteAsync(RequestIdentity.Runner(context), jobId, request, context.RequestAborted).ConfigureAwait(false));
        });

        runner.MapPost("/jobs/{jobId}/release", async (string jobId, HttpContext context, Orchestrator orchestrator) =>
        {
            var request = await ReadAsync(context, RunnerProtocolJsonContext.Default.JobReleaseRequest).ConfigureAwait(false);
            return Lease(await orchestrator.ReleaseAsync(RequestIdentity.Runner(context), jobId, request, context.RequestAborted).ConfigureAwait(false));
        });
    }

    private static async Task<T> ReadAsync<T>(HttpContext context, JsonTypeInfo<T> type)
    {
        if (context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
        {
            throw new OrchestrationException(OrchestrationFailure.Invalid, "Runner messages are application/json.");
        }

        return await JsonSerializer.DeserializeAsync(context.Request.Body, type, context.RequestAborted).ConfigureAwait(false)
            ?? throw new OrchestrationException(OrchestrationFailure.Invalid, "The request body is empty.");
    }

    /// <summary>A lease answer: 200 when the lease is valid, 409 (with the same body) when it is not.</summary>
    private static IResult Lease(JobLeaseResponse response) =>
        Results.Json(response, RunnerProtocolJsonContext.Default.JobLeaseResponse, statusCode: response.LeaseValid ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);

    private static IResult Error(int status, string message) =>
        Results.Json(new RunnerErrorResponse(message), RunnerProtocolJsonContext.Default.RunnerErrorResponse, statusCode: status);
}
