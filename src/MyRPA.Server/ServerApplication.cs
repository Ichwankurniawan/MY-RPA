using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using MyRPA.Activities;
using MyRPA.Core.Activities;
using MyRPA.Core.Diagnostics;
using MyRPA.Execution.Hosting;
using MyRPA.Plugins;
using MyRPA.Runtime;
using MyRPA.Storage;
using MyRPA.Workflow.Serialization;
using MyRPA.Workflow.Validation;
using MyRPA.Workflow.Values;

namespace MyRPA.Server;

/// <summary>A started server.</summary>
internal sealed class RunningServer(WebApplication app, Uri baseUri, PluginSet? plugins) : IAsyncDisposable
{
    public WebApplication App { get; } = app;

    /// <summary>The loopback address the server listens on.</summary>
    public Uri BaseUri { get; } = baseUri;

    /// <summary>The one-time start link (opens a browser session), or null once used.</summary>
    public Uri? StartUri => App.Services.GetRequiredService<LocalSessions>().StartToken is { } token ? new Uri(BaseUri, $"/?token={token}") : null;

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync().ConfigureAwait(false);
        await App.DisposeAsync().ConfigureAwait(false);
        if (plugins is not null)
        {
            await plugins.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Composes and runs the local-mode control plane (ADR-0022, ADR-0024, ADR-0025).</summary>
internal static class ServerApplication
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (ServerCommandLine.Parse(args, out var usageError) is not { } options)
        {
            await error.WriteLineAsync($"Error: {usageError}\nUsage: {ServerCommandLine.Usage}").ConfigureAwait(false);
            return 2;
        }

        PluginSet? plugins;
        try
        {
            plugins = await LoadPluginsAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (PluginConfigurationException ex)
        {
            await error.WriteLineAsync($"Error: {ex.Message}").ConfigureAwait(false);
            return 5;
        }

        if (plugins is { HasRequiredFailures: true })
        {
            foreach (var diagnostic in plugins.Diagnostics)
            {
                await error.WriteLineAsync(diagnostic.ToString()).ConfigureAwait(false);
            }

            await plugins.DisposeAsync().ConfigureAwait(false);
            await error.WriteLineAsync("Error: plugins failed to load; the server was not started.").ConfigureAwait(false);
            return 5;
        }

        await using var server = await StartAsync(options, plugins, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"MyRPA Server (local mode) listening on {server.BaseUri}").ConfigureAwait(false);
        await output.WriteLineAsync($"Open this link in your browser (valid once): {server.StartUri}").ConfigureAwait(false);
        await server.App.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public static async Task<PluginSet?> LoadPluginsAsync(ServerOptions options, CancellationToken cancellationToken)
    {
        if (options.PluginDirectories.Count == 0 && options.PluginConfiguration is null)
        {
            return null;
        }

        var hostOptions = await PluginConfigurationFile.CreateHostOptionsAsync(options.PluginDirectories, options.PluginConfiguration, cancellationToken).ConfigureAwait(false);
        return await PluginLoader.LoadAsync(hostOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds and starts the server; the caller disposes it (and with it the plugins).</summary>
    public static async Task<RunningServer> StartAsync(ServerOptions options, PluginSet? plugins, CancellationToken cancellationToken, TimeProvider? time = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "MyRPA.Server",
            // Configuration comes from the install directory, never from the caller's working directory.
            ContentRootPath = AppContext.BaseDirectory,
            Args = [],
        });
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, options.Port);
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes;
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(c => c.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Workflow log messages (Core.Log, Information) must reach the per-run log router (ADR-0023); the console only
        // shows warnings, so they go to the runs' event streams, not to the server's output.
        builder.Logging.AddFilter(DiagnosticNames.WorkflowLogCategory, LogLevel.Information);
        builder.Logging.AddFilter<Microsoft.Extensions.Logging.Console.ConsoleLoggerProvider>(DiagnosticNames.WorkflowLogCategory, LogLevel.Warning);

        var services = builder.Services;
        if (time is not null)
        {
            services.AddSingleton(time);
        }

        services.AddMyRpaRuntime();
        services.AddMyRpaActivities();
        services.AddMyRpaStorage();
        if (plugins is not null)
        {
            services.AddMyRpaPlugins(plugins);
        }

        services.AddMyRpaExecutionHosting(options.ConfigureHosting);
        services.AddSingleton(options);
        services.AddSingleton<ProjectStore>();
        services.AddSingleton<LocalSessions>();
        services.AddSingleton<EventStreams>();
        if (options.WebRoot is { } webRoot)
        {
            // Factory-created, so the container disposes it. Hidden, dot-prefixed and system files are never served.
            services.AddSingleton(_ => new PhysicalFileProvider(webRoot, ExclusionFilters.Sensitive));
        }

        var app = builder.Build();
        app.UseLocalSecurity();
        if (options.WebRoot is not null)
        {
            // ADR-0022: the server serves the built Web Studio from its own origin, behind the same guard (Host check,
            // CSP, nosniff); the assets hold no data, so they need no session. Unknown file types are not served.
            app.UseStaticFiles(new StaticFileOptions { FileProvider = app.Services.GetRequiredService<PhysicalFileProvider>() });
        }

        MapEndpoints(app);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        plugins?.VerifyProviders(app.Services);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new RunningServer(app, new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal)), plugins);
    }

    private static void MapEndpoints(WebApplication app)
    {
        // Start link: exchange the one-time token for a session cookie, then drop the token from the address bar.
        app.MapGet("/", (HttpContext context, LocalSessions sessions, ServerOptions options) =>
        {
            if (context.Request.Query["token"].FirstOrDefault() is { } token)
            {
                if (sessions.Redeem(token) is not { } session)
                {
                    return Results.Text("This start link was already used or has expired. Restart the server for a new one.", statusCode: StatusCodes.Status403Forbidden);
                }

                context.Response.Cookies.Append(LocalSessions.CookieName, session, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true });
                return Results.Redirect("/", permanent: false, preserveMethod: false);
            }

            if (options.WebRoot is { } webRoot)
            {
                context.Response.Headers.CacheControl = "no-cache";
                return Results.File(Path.Combine(webRoot, "index.html"), "text/html; charset=utf-8");
            }

            return Results.Text("MyRPA Server is running. Start it with --web <dir> to serve the Web Studio.", "text/plain");
        });

        var api = app.MapGroup("/api");

        api.MapGet("/info", (ServerOptions options) => Results.Json(new
        {
            name = "MyRPA.Server",
            version = typeof(ServerApplication).Assembly.GetName().Version?.ToString(),
            mode = "local",
            workflowSchemaVersions = new[] { MyRPA.Workflow.WorkflowSchemaVersion.Current.ToString() },
            projects = options.Projects.Select(p => p.Name),
        }));

        api.MapGet("/activities", (IActivityCatalog catalog) => Results.Text(ActivityCatalogJson.Write(catalog.Descriptors), "application/json"));

        api.MapGet("/plugins", (IServiceProvider services) =>
        {
            var registry = services.GetService<IPluginRegistry>();
            return Results.Json(new
            {
                plugins = (registry?.Plugins ?? []).Select(p => new
                {
                    id = p.Manifest.Id.Value,
                    name = p.Manifest.Name,
                    version = p.Manifest.Version.ToString(),
                    sha256 = p.Digest,
                    activities = p.Manifest.Activities.Select(a => a.Value),
                }),
                diagnostics = (registry?.Diagnostics ?? []).Select(d => new { code = d.Code, severity = d.Severity.ToString(), message = d.Message, pluginId = d.PluginId }),
            });
        });

        MapProjects(api);
        MapRuns(api);
        MapStreams(api);
    }

    private static void MapProjects(RouteGroupBuilder api)
    {
        api.MapGet("/projects", (ProjectStore store) => Results.Json(new { projects = store.Projects.Select(p => new { name = p.Name }) }));

        api.MapGet("/projects/{project}/workflows", (string project, ProjectStore store) =>
        {
            var root = store.Projects.FirstOrDefault(p => string.Equals(p.Name, project, StringComparison.OrdinalIgnoreCase));
            return root is null ? NotFound($"Unknown project '{project}'.") : Results.Json(new { workflows = ProjectStore.List(root) });
        });

        api.MapGet("/projects/{project}/workflows/{**path}", async (string project, string path, ProjectStore store, HttpContext context) =>
        {
            if (!store.TryResolve(project, path, out var file, out var error))
            {
                return BadRequest(error);
            }

            if (await store.ReadAsync(file, context.RequestAborted).ConfigureAwait(false) is not { } read)
            {
                return NotFound($"'{path}' does not exist.");
            }

            context.Response.Headers.ETag = read.ETag;
            return Results.Bytes(read.Content, "application/json");
        });

        api.MapPut("/projects/{project}/workflows/{**path}", async (string project, string path, ProjectStore store, HttpContext context) =>
        {
            if (!store.TryResolve(project, path, out var file, out var error))
            {
                return BadRequest(error);
            }

            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted).ConfigureAwait(false);
            var content = buffer.ToArray();
            if (!ProjectStore.IsJsonObject(content))
            {
                return BadRequest("A workflow file must be a JSON object.");
            }

            var ifMatch = context.Request.Headers.IfMatch.FirstOrDefault();
            var ifNoneMatchAny = context.Request.Headers.IfNoneMatch.FirstOrDefault() == "*";
            var (outcome, etag) = await store.WriteAsync(file, content, ifMatch, ifNoneMatchAny, context.RequestAborted).ConfigureAwait(false);
            if (etag is not null)
            {
                context.Response.Headers.ETag = etag;
            }

            return outcome switch
            {
                FileWriteOutcome.Created => Results.StatusCode(StatusCodes.Status201Created),
                FileWriteOutcome.Updated => Results.NoContent(),
                FileWriteOutcome.PreconditionRequired => Problem(StatusCodes.Status428PreconditionRequired, "Send If-Match with the file's ETag, or If-None-Match: * to create it."),
                _ => Problem(StatusCodes.Status412PreconditionFailed, "The file changed since it was read (or already exists)."),
            };
        });

        api.MapDelete("/projects/{project}/workflows/{**path}", async (string project, string path, ProjectStore store, HttpContext context) =>
        {
            if (!store.TryResolve(project, path, out var file, out var error))
            {
                return BadRequest(error);
            }

            return await store.DeleteAsync(file, context.Request.Headers.IfMatch.FirstOrDefault(), context.RequestAborted).ConfigureAwait(false) switch
            {
                FileWriteOutcome.Deleted => Results.NoContent(),
                FileWriteOutcome.NotFound => NotFound($"'{path}' does not exist."),
                FileWriteOutcome.PreconditionRequired => Problem(StatusCodes.Status428PreconditionRequired, "Send If-Match with the file's ETag."),
                _ => Problem(StatusCodes.Status412PreconditionFailed, "The file changed since it was read."),
            };
        });

        api.MapPost("/validate", (ValidateRequest request, WorkflowLoader loader) =>
            request.Document is not { ValueKind: JsonValueKind.Object } document
                ? BadRequest("'document' must be a workflow JSON object.")
                : Results.Json(Validation(loader.Load(document.GetRawText()))));
    }

    private static void MapRuns(RouteGroupBuilder api)
    {
        api.MapPost("/runs", async (StartRunRequest request, ProjectStore store, WorkflowLoader loader, ExecutionHost host, HttpContext context) =>
        {
            if (request.Project is null)
            {
                return BadRequest("'project' and 'path' are required.");
            }

            if (!store.TryResolve(request.Project, request.Path, out var file, out var error))
            {
                return BadRequest(error);
            }

            // An unsaved buffer runs as if it were saved at `path`, so sub-workflows resolve from there (ADR-0012, ADR-0027).
            string json;
            if (request.Document is { ValueKind: JsonValueKind.Object } document)
            {
                json = document.GetRawText();
            }
            else if (await store.ReadAsync(file, context.RequestAborted).ConfigureAwait(false) is { } read)
            {
                json = Encoding.UTF8.GetString(read.Content);
            }
            else
            {
                return NotFound($"'{request.Path}' does not exist.");
            }

            var loaded = loader.Load(json);
            if (!loaded.IsValid)
            {
                return Results.Json(Validation(loaded), statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in request.Arguments ?? [])
            {
                var definition = loaded.Workflow.Arguments.FirstOrDefault(a => a.Name == name && a.IsInput);
                if (definition is null)
                {
                    return BadRequest($"'{name}' is not an input argument of the workflow.");
                }

                if (!WorkflowValues.TryFromJson(value, definition.Type, out var converted, out var conversionError))
                {
                    return BadRequest($"Argument '{name}': {conversionError}");
                }

                arguments[name] = converted;
            }

            if (request.TimeoutMs is <= 0)
            {
                return BadRequest("'timeoutMs' must be positive.");
            }

            var run = host.Start(loaded.Workflow, new ExecutionStartRequest
            {
                Arguments = arguments,
                Timeout = request.TimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
                Location = file,
            });
            return Results.Json(new { runId = run.RunId }, statusCode: StatusCodes.Status202Accepted);
        });

        api.MapGet("/runs/{runId}", (string runId, ExecutionHost host) =>
            host.TryGet(runId, out var run) ? Results.Text(RunJson(run), "application/json") : NotFound($"Unknown run '{runId}'."));

        api.MapPost("/runs/{runId}/cancel", (string runId, ExecutionHost host) =>
            host.Cancel(runId) ? Results.Accepted() : host.TryGet(runId, out _) ? Problem(StatusCodes.Status409Conflict, "The run has already finished.") : NotFound($"Unknown run '{runId}'."));
    }

    private static void MapStreams(RouteGroupBuilder api)
    {
        api.MapPost("/streams", (HttpContext context, EventStreams streams) =>
            streams.Create(Session(context)) is { } stream
                ? Results.Json(new { streamId = stream.Id }, statusCode: StatusCodes.Status201Created)
                : Problem(StatusCodes.Status429TooManyRequests, "Too many open event streams."));

        api.MapGet("/streams/{streamId}", async (string streamId, HttpContext context, EventStreams streams) =>
        {
            if (streams.Find(streamId, Session(context)) is not { } stream)
            {
                await NotFound($"Unknown stream '{streamId}'.").ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            await stream.ServeAsync(context.Response, context.Request.Headers["Last-Event-ID"].FirstOrDefault(), context.RequestAborted).ConfigureAwait(false);
        });

        api.MapDelete("/streams/{streamId}", (string streamId, HttpContext context, EventStreams streams) =>
        {
            if (streams.Find(streamId, Session(context)) is not { } stream)
            {
                return NotFound($"Unknown stream '{streamId}'.");
            }

            streams.Close(stream);
            return Results.NoContent();
        });

        api.MapPost("/streams/{streamId}/subscriptions", (string streamId, SubscribeRequest request, HttpContext context, EventStreams streams, ExecutionHost host) =>
        {
            if (streams.Find(streamId, Session(context)) is not { } stream)
            {
                return NotFound($"Unknown stream '{streamId}'.");
            }

            if (request.RunId is null || !host.TryGet(request.RunId, out var run))
            {
                return NotFound($"Unknown run '{request.RunId}'.");
            }

            return stream.Subscribe(run, Math.Max(0, request.AfterSequence ?? 0)) switch
            {
                SubscribeOutcome.Subscribed => Results.StatusCode(StatusCodes.Status201Created),
                SubscribeOutcome.AlreadySubscribed => Problem(StatusCodes.Status409Conflict, "The stream already follows this run."),
                _ => Problem(StatusCodes.Status429TooManyRequests, "Too many runs on this stream."),
            };
        });

        api.MapDelete("/streams/{streamId}/subscriptions/{runId}", (string streamId, string runId, HttpContext context, EventStreams streams) =>
            streams.Find(streamId, Session(context)) is { } stream && stream.Unsubscribe(runId) ? Results.NoContent() : NotFound("Unknown stream or subscription."));
    }

    private static object Validation(WorkflowLoadResult result) => new
    {
        valid = result.IsValid,
        diagnostics = result.Diagnostics.Select(d => new { code = d.Code, severity = d.Severity.ToString(), message = d.Message, path = d.Path, nodeId = d.NodeId }),
    };

    private static string RunJson(ExecutionHandle run)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("runId", run.RunId);
            writer.WriteString("state", run.State.ToString());
            writer.WriteNumber("lastSequence", run.LastSequence);
            if (run.Completion.IsCompletedSuccessfully)
            {
                var result = run.Completion.Result;
                writer.WriteStartObject("result");
                writer.WriteString("status", result.Status.ToString());
                writer.WriteString("executionId", result.ExecutionId.ToString());
                writer.WriteString("correlationId", result.CorrelationId.Value);
                writer.WriteString("workflowId", result.WorkflowId.Value);
                writer.WriteNumber("durationMs", result.Duration.TotalMilliseconds);
                writer.WritePropertyName("outputs");
                WorkflowValues.WriteJson(writer, result.Outputs);
                if (result.Error is { } error)
                {
                    writer.WriteStartObject("error");
                    writer.WriteString("code", error.Code);
                    writer.WriteString("message", error.Message);
                    writer.WriteString("nodeId", error.NodeId);
                    writer.WriteString("activityType", error.ActivityType);
                    writer.WriteString("errorType", error.ErrorType);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Session(HttpContext context) => context.Request.Cookies[LocalSessions.CookieName]!;

    private static IResult BadRequest(string message) => Results.Json(new { error = message }, statusCode: StatusCodes.Status400BadRequest);

    private static IResult NotFound(string message) => Results.Json(new { error = message }, statusCode: StatusCodes.Status404NotFound);

    private static IResult Problem(int status, string message) => Results.Json(new { error = message }, statusCode: status);
}

/// <summary>Body of <c>POST /api/validate</c>.</summary>
internal sealed record ValidateRequest(JsonElement? Document);

/// <summary>Body of <c>POST /api/runs</c>.</summary>
internal sealed record StartRunRequest(string? Project, string? Path, JsonElement? Document, Dictionary<string, JsonElement>? Arguments, int? TimeoutMs);

/// <summary>Body of <c>POST /api/streams/{streamId}/subscriptions</c>.</summary>
internal sealed record SubscribeRequest(string? RunId, long? AfterSequence);
