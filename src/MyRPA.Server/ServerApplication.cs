using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using MyRPA.Activities;
using MyRPA.Browser.Contracts;
using MyRPA.Core.Activities;
using MyRPA.Core.Diagnostics;
using MyRPA.Execution.Hosting;
using MyRPA.Plugins;
using MyRPA.Runtime;
using MyRPA.Storage;
using MyRPA.Workflow;
using MyRPA.Workflow.Expressions;
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
    /// <summary>Most breakpoints a debug run accepts (ADR-0040).</summary>
    private const int MaxBreakpoints = 10_000;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        // ADR-0046: with no project named at all, the projects folder is Documents/Laconi Projects.
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var defaultProjectsRoot = documents.Length > 0 ? Path.Combine(documents, "Laconi Projects") : null;
        if (ServerCommandLine.Parse(args, out var usageError, Path.Combine(AppContext.BaseDirectory, "wwwroot"), defaultProjectsRoot) is not { } options)
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
        await output.WriteLineAsync(options.WebRoot is { } webRoot ? $"Web Studio: {webRoot}" : "Web Studio: not built (run 'npm run build' in web/studio, then build the server, or pass --web <dir>)").ConfigureAwait(false);
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
        services.AddSingleton<Recordings>();
        services.AddSingleton<DebugRuns>();
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

        api.MapGet("/info", (ServerOptions options, ProjectStore store) => Results.Json(new
        {
            name = "MyRPA.Server",
            version = typeof(ServerApplication).Assembly.GetName().Version?.ToString(),
            mode = "local",
            workflowSchemaVersions = new[] { MyRPA.Workflow.WorkflowSchemaVersion.Initial.ToString(), MyRPA.Workflow.WorkflowSchemaVersion.Graphs.ToString() },
            projects = store.Projects.Select(p => p.Name),
            // The workflow named on the command line (--open), which the Studio opens after connecting (W6).
            open = options.Open is { } open ? new { project = open.Project, path = open.Path } : null,
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
        MapExpressions(api);
        MapStreams(api);
        MapRecordings(api);
    }

    private static void MapProjects(RouteGroupBuilder api)
    {
        // ADR-0046: the projects, which ones the Studio may delete (those of the projects folder), and where new ones go.
        api.MapGet("/projects", (ProjectStore store, ServerOptions options) => Results.Json(new
        {
            projects = store.Projects.Select(p => new { name = p.Name, removable = p.InRoot }),
            projectsRoot = options.ProjectsRoot,
        }));

        api.MapPost("/projects", async (ProjectRequest request, ProjectStore store, ServerOptions options, HttpContext context) =>
        {
            if (options.ProjectsRoot is null)
            {
                return Problem(StatusCodes.Status409Conflict, "This server has no projects folder: start it with --projects-root to create projects.");
            }

            if (!ProjectStore.IsValidProjectName(request.Name, out var error))
            {
                return BadRequest($"'name': {error}");
            }

            return await store.CreateProjectAsync(request.Name!, context.RequestAborted).ConfigureAwait(false) == FileWriteOutcome.Created
                ? Results.Json(new { name = request.Name }, statusCode: StatusCodes.Status201Created)
                : Problem(StatusCodes.Status409Conflict, $"A project or folder named '{request.Name}' already exists.");
        });

        // Delete moves the folder to the projects folder's .trash; a --project is never deleted from the Studio.
        api.MapDelete("/projects/{project}", async (string project, ProjectStore store, HttpContext context) =>
        {
            if (store.Find(project) is not { } root)
            {
                return NotFound($"Unknown project '{project}'.");
            }

            if (!root.InRoot)
            {
                return Problem(StatusCodes.Status409Conflict, $"'{root.Name}' was named with --project; remove it from the server's command line instead.");
            }

            var (trashed, error) = await store.TrashProjectAsync(root, context.RequestAborted).ConfigureAwait(false);
            return trashed is not null ? Results.Json(new { name = root.Name, trash = trashed }) : Problem(StatusCodes.Status409Conflict, error!);
        });

        api.MapGet("/projects/{project}/workflows", (string project, ProjectStore store) =>
        {
            var root = store.Find(project);
            return root is null ? NotFound($"Unknown project '{project}'.") : Results.Json(new { workflows = ProjectStore.List(root), folders = ProjectStore.ListFolders(root) });
        });

        // Folders for organising workflows: created inside the project only (same path rules as files, no links).
        api.MapPost("/projects/{project}/folders", async (string project, FolderRequest request, ProjectStore store, HttpContext context) =>
        {
            if (!store.TryResolve(project, request.Path, out var full, out var error, folder: true))
            {
                return BadRequest($"'path': {error}");
            }

            return await store.CreateFolderAsync(full, context.RequestAborted).ConfigureAwait(false) == FileWriteOutcome.Created
                ? Results.Json(new { path = request.Path }, statusCode: StatusCodes.Status201Created)
                : Problem(StatusCodes.Status409Conflict, $"'{request.Path}' already exists.");
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

        // Rename or move within a project (W6, ADR-0031): atomic, conditional on the source's ETag, never overwrites.
        api.MapPost("/projects/{project}/move", async (string project, MoveRequest request, ProjectStore store, HttpContext context) =>
        {
            if (!store.TryResolve(project, request.From, out var from, out var fromError))
            {
                return BadRequest($"'from': {fromError}");
            }

            if (!store.TryResolve(project, request.To, out var to, out var toError))
            {
                return BadRequest($"'to': {toError}");
            }

            if (string.Equals(from, to, StringComparison.Ordinal))
            {
                return BadRequest("'from' and 'to' are the same file.");
            }

            var (outcome, etag) = await store.MoveAsync(from, to, context.Request.Headers.IfMatch.FirstOrDefault(), context.RequestAborted).ConfigureAwait(false);
            if (etag is not null)
            {
                context.Response.Headers.ETag = etag;
            }

            return outcome switch
            {
                FileWriteOutcome.Moved => Results.Json(new { path = request.To }),
                FileWriteOutcome.NotFound => NotFound($"'{request.From}' does not exist."),
                FileWriteOutcome.TargetExists => Problem(StatusCodes.Status409Conflict, $"'{request.To}' already exists."),
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
        api.MapPost("/runs", async (StartRunRequest request, ProjectStore store, WorkflowLoader loader, ExecutionHost host, DebugRuns debugRuns, HttpContext context) =>
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

            // Text typed by a person (the Web Studio's run dialog), parsed like the CLI's --arg and the WPF prompt.
            foreach (var (name, text) in request.ArgumentText ?? [])
            {
                var definition = loaded.Workflow.Arguments.FirstOrDefault(a => a.Name == name && a.IsInput);
                if (definition is null)
                {
                    return BadRequest($"'{name}' is not an input argument of the workflow.");
                }

                if (arguments.ContainsKey(name))
                {
                    return BadRequest($"Argument '{name}' is given both as a value and as text.");
                }

                if (text is null)
                {
                    return BadRequest($"Argument '{name}': the text must be a string.");
                }

                if (!WorkflowValues.TryParseText(text, definition.Type, out var converted, out var conversionError))
                {
                    return BadRequest($"Argument '{name}': {conversionError}");
                }

                arguments[name] = converted;
            }

            if (request.TimeoutMs is <= 0)
            {
                return BadRequest("'timeoutMs' must be positive.");
            }

            DebugOptions? debug = null;
            if (request.Debug is { } debugRequest)
            {
                if (!TryBreakpoints(debugRequest.Breakpoints, loaded.Workflow.Id.Value, out var breakpoints, out var breakpointError))
                {
                    return BadRequest(breakpointError);
                }

                debug = new DebugOptions { Breakpoints = breakpoints, PauseAtStart = debugRequest.PauseAtStart };
            }

            var run = host.Start(loaded.Workflow, new ExecutionStartRequest
            {
                Arguments = arguments,
                Timeout = request.TimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
                Location = file,
                Debug = debug,
            });
            if (debug is not null)
            {
                debugRuns.Add(run.RunId, Session(context), loaded.Workflow.Id.Value);
            }

            return Results.Json(new { runId = run.RunId }, statusCode: StatusCodes.Status202Accepted);
        });

        api.MapGet("/runs/{runId}", (string runId, ExecutionHost host) =>
            host.TryGet(runId, out var run) ? Results.Text(RunJson(run), "application/json") : NotFound($"Unknown run '{runId}'."));

        api.MapPost("/runs/{runId}/cancel", (string runId, ExecutionHost host) =>
            host.Cancel(runId) ? Results.Accepted() : host.TryGet(runId, out _) ? Problem(StatusCodes.Status409Conflict, "The run has already finished.") : NotFound($"Unknown run '{runId}'."));

        MapDebug(api);
    }

    /// <summary>Debugging a run (ADR-0040): only the session that started a debug run may use these.</summary>
    private static void MapDebug(RouteGroupBuilder api)
    {
        api.MapGet("/runs/{runId}/debug", (string runId, DebugRuns debugRuns, HttpContext context) =>
            debugRuns.Find(runId, Session(context)) is { } found
                ? Results.Text(DebugJson(found.Debug, found.WorkflowId), "application/json")
                : NotFound($"Unknown debug run '{runId}'."));

        api.MapPost("/runs/{runId}/debug", (string runId, DebugCommandRequest request, DebugRuns debugRuns, HttpContext context) =>
        {
            if (debugRuns.Find(runId, Session(context)) is not { } found)
            {
                return NotFound($"Unknown debug run '{runId}'.");
            }

            if (request.Command == "pause")
            {
                return found.Debug.RequestPause() ? Results.Accepted() : Problem(StatusCodes.Status409Conflict, "The run is already paused or has finished.");
            }

            DebugCommand? command = request.Command switch
            {
                "continue" => DebugCommand.Continue,
                "stepInto" => DebugCommand.StepInto,
                "stepOver" => DebugCommand.StepOver,
                "stepOut" => DebugCommand.StepOut,
                _ => null,
            };
            if (command is not { } resume)
            {
                return BadRequest("'command' must be continue, stepInto, stepOver, stepOut or pause.");
            }

            return found.Debug.Resume(resume) ? Results.Accepted() : Problem(StatusCodes.Status409Conflict, "The run is not paused.");
        });

        api.MapPut("/runs/{runId}/breakpoints", (string runId, BreakpointsRequest request, DebugRuns debugRuns, HttpContext context) =>
        {
            if (debugRuns.Find(runId, Session(context)) is not { } found)
            {
                return NotFound($"Unknown debug run '{runId}'.");
            }

            if (!TryBreakpoints(request.Breakpoints, found.WorkflowId, out var breakpoints, out var error))
            {
                return BadRequest(error);
            }

            found.Debug.SetBreakpoints(breakpoints);
            return Results.NoContent();
        });
    }

    /// <summary>Breakpoints are node ids of the run's workflow (at most <see cref="MaxBreakpoints"/>).</summary>
    private static bool TryBreakpoints(List<string?>? nodeIds, string workflowId, out List<DebugBreakpoint> breakpoints, out string error)
    {
        breakpoints = [];
        error = string.Empty;
        if (nodeIds is { Count: > MaxBreakpoints })
        {
            error = $"At most {MaxBreakpoints} breakpoints.";
            return false;
        }

        foreach (var nodeId in nodeIds ?? [])
        {
            if (string.IsNullOrEmpty(nodeId))
            {
                error = "Breakpoints must be node ids.";
                return false;
            }

            breakpoints.Add(new DebugBreakpoint(workflowId, nodeId));
        }

        return true;
    }

    /// <summary>The paused state with its values (never part of the event stream) and the run's breakpoints.</summary>
    private static string DebugJson(DebugSession debug, string workflowId)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (debug.Paused is { } paused)
            {
                writer.WriteStartObject("paused");
                writer.WriteString("executionId", paused.ExecutionId);
                writer.WriteString("parentExecutionId", paused.ParentExecutionId);
                writer.WriteString("workflowId", paused.WorkflowId);
                writer.WriteString("nodeId", paused.NodeId);
                writer.WriteString("activityType", paused.ActivityType);
                writer.WriteString("reason", paused.Reason switch
                {
                    DebugPauseReason.Breakpoint => "breakpoint",
                    DebugPauseReason.Step => "step",
                    _ => "pause",
                });
                writer.WriteStartArray("values");
                foreach (var value in paused.Values)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", value.Name);
                    writer.WriteString("kind", value.Kind.ToString());
                    writer.WriteString("type", value.Type.ToString());
                    writer.WritePropertyName("value");
                    WorkflowValues.WriteJson(writer, value.Value);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("paused");
            }

            writer.WriteStartArray("breakpoints");
            foreach (var breakpoint in debug.Breakpoints.Where(b => b.WorkflowId == workflowId).OrderBy(b => b.NodeId, StringComparer.Ordinal))
            {
                writer.WriteStringValue(breakpoint.NodeId);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Expression assist (ADR-0041): the functions, the names in scope at a node and the uses of a name, computed by the
    /// loader's own validation walk. Read-only: nothing is stored, and the document is the one being edited.
    /// </summary>
    private static void MapExpressions(RouteGroupBuilder api)
    {
        api.MapGet("/expressions/functions", () => Results.Json(ExpressionFunctions.Functions.Select(f => new
        {
            name = f.Name,
            minArguments = f.MinArguments,
            maxArguments = f.MaxArguments,
            signature = f.Signature,
            description = f.Description,
        })));

        api.MapPost("/expressions/scope", (ExpressionScopeRequest request, WorkflowLoader loader) =>
        {
            if (DocumentAndPathRefusal(request.Document, request.Path) is { } refusal)
            {
                return refusal;
            }

            var index = loader.IndexNames(request.Document!.Value.GetRawText());
            return Results.Json(new { names = index.InScope(request.Path!).Select(SymbolJson) });
        });

        api.MapPost("/expressions/references", (ExpressionReferencesRequest request, WorkflowLoader loader) =>
        {
            if (DocumentAndPathRefusal(request.Document, request.Path) is { } refusal)
            {
                return refusal;
            }

            if (!WorkflowNames.IsValid(request.Name))
            {
                return BadRequest("'name' must be a variable, argument or local name.");
            }

            var index = loader.IndexNames(request.Document!.Value.GetRawText());
            var declaration = index.Resolve(request.Path!, request.Name!);
            IReadOnlyList<WorkflowNameReference> references = declaration is null ? [] : index.ReferencesTo(declaration);
            return Results.Json(new
            {
                declaration = declaration is null ? null : SymbolJson(declaration),
                references = references.Select(r => new { path = r.Path, start = r.Start, length = r.Length, declaration = r.IsDeclaration }),
            });
        });
    }

    private static IResult? DocumentAndPathRefusal(JsonElement? document, string? path) =>
        document is not { ValueKind: JsonValueKind.Object } ? BadRequest("'document' must be a workflow JSON object.")
        : string.IsNullOrEmpty(path) || path.Length > 4096 || !path.StartsWith('$') ? BadRequest("'path' must be a JSON path such as $.root.properties.message.")
        : null;

    private static object SymbolJson(WorkflowSymbol symbol) => new
    {
        name = symbol.Name,
        kind = symbol.Kind.ToString(),
        type = symbol.Type.ToString(),
        direction = symbol.Direction?.ToString(),
        path = symbol.DeclarationPath,
    };

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

        api.MapPost("/streams/{streamId}/subscriptions", (string streamId, SubscribeRequest request, HttpContext context, EventStreams streams, ExecutionHost host, Recordings recordings) =>
        {
            if (streams.Find(streamId, Session(context)) is not { } stream)
            {
                return NotFound($"Unknown stream '{streamId}'.");
            }

            if (request.RecordingId is { } recordingId)
            {
                return recordings.Find(recordingId, Session(context)) is { } recording
                    ? Subscribed(stream.Subscribe(recording, Math.Max(0, request.AfterSequence ?? 0)), "recording")
                    : NotFound($"Unknown recording '{recordingId}'.");
            }

            if (request.RunId is null || !host.TryGet(request.RunId, out var run))
            {
                return NotFound($"Unknown run '{request.RunId}'.");
            }

            return stream.Subscribe(new RunSource(run), Math.Max(0, request.AfterSequence ?? 0)) switch
            {
                SubscribeOutcome.Subscribed => Results.StatusCode(StatusCodes.Status201Created),
                SubscribeOutcome.AlreadySubscribed => Problem(StatusCodes.Status409Conflict, "The stream already follows this run."),
                _ => Problem(StatusCodes.Status429TooManyRequests, "Too many runs on this stream."),
            };
        });

        api.MapDelete("/streams/{streamId}/subscriptions/{runId}", (string streamId, string runId, HttpContext context, EventStreams streams) =>
            streams.Find(streamId, Session(context)) is { } stream && stream.Unsubscribe(runId) ? Results.NoContent() : NotFound("Unknown stream or subscription."));
    }

    private static IResult Subscribed(SubscribeOutcome outcome, string what) => outcome switch
    {
        SubscribeOutcome.Subscribed => Results.StatusCode(StatusCodes.Status201Created),
        SubscribeOutcome.AlreadySubscribed => Problem(StatusCodes.Status409Conflict, $"The stream already follows this {what}."),
        _ => Problem(StatusCodes.Status429TooManyRequests, "Too many subscriptions on this stream."),
    };

    // ADR-0039: recordings. A recording is started from the Studio, records in a visible browser on this machine, and
    // sends its steps to the tab's event stream; nothing it records is executed.
    private static void MapRecordings(RouteGroupBuilder api)
    {
        api.MapGet("/recordings", (Recordings recordings) => Results.Json(new { available = recordings.Unavailable is null, reason = recordings.Unavailable }));

        api.MapPost("/recordings", async (StartRecordingRequest request, HttpContext context, Recordings recordings) =>
        {
            if (!Uri.TryCreate(request.StartUrl, UriKind.Absolute, out var url))
            {
                return BadRequest("startUrl must be an absolute http or https URL.");
            }

            var (handle, status, error) = await recordings.StartAsync(Session(context), url, context.RequestAborted).ConfigureAwait(false);
            return handle is null
                ? Problem(status, error!)
                : Results.Json(new { recordingId = handle.Id, startUrl = handle.StartUrl.AbsoluteUri }, statusCode: StatusCodes.Status201Created);
        });

        // The reviewed steps (as the Studio kept and edited them) become activities of the plugin that recorded them.
        api.MapPost("/recordings/generate", (GenerateRecordingRequest request, Recordings recordings) =>
        {
            if (!Uri.TryCreate(request.StartUrl, UriKind.Absolute, out var url))
            {
                return BadRequest("startUrl must be an absolute http or https URL.");
            }

            if (request.Steps is not { Count: <= 1000 } steps)
            {
                return BadRequest("steps must be a list of at most 1000 recorded steps.");
            }

            var recorded = new List<RecordedStep>();
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                if (!Enum.TryParse<RecordedStepKind>(step.Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind) || int.TryParse(step.Kind, out _))
                {
                    return BadRequest($"Step {i + 1}: '{step.Kind}' is not a recorded step kind.");
                }

                recorded.Add(new RecordedStep(i + 1, kind, DateTimeOffset.UnixEpoch)
                {
                    Selector = step.Selector,
                    Element = step.Element,
                    Text = step.Secret ? null : step.Text,
                    Secret = step.Secret,
                    Values = step.Values ?? [],
                    Url = step.Url,
                    FileName = step.FileName,
                });
            }

            var (json, status, error) = recordings.Generate(url, recorded);
            return json is null ? Problem(status, error!) : Results.Text(json, "application/json");
        });

        api.MapGet("/recordings/{recordingId}", (string recordingId, HttpContext context, Recordings recordings) =>
            recordings.Find(recordingId, Session(context)) is { } handle
                ? Results.Text(RecordingJson(handle), "application/json")
                : NotFound($"Unknown recording '{recordingId}'."));

        api.MapDelete("/recordings/{recordingId}", async (string recordingId, HttpContext context, Recordings recordings) =>
        {
            if (recordings.Find(recordingId, Session(context)) is not { } handle)
            {
                return NotFound($"Unknown recording '{recordingId}'.");
            }

            await handle.StopAsync().ConfigureAwait(false);
            return Results.NoContent();
        });
    }

    private static string RecordingJson(RecordingHandle handle)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("recordingId", handle.Id);
            writer.WriteString("startUrl", handle.StartUrl.AbsoluteUri);
            writer.WriteBoolean("active", handle.End is null);
            if (handle.End is { } end)
            {
                writer.WriteString("endReason", end.Reason.ToString());
            }

            writer.WritePropertyName("steps");
            JsonSerializer.Serialize(writer, handle.Steps, MyRPA.Contracts.Execution.ContractsJsonContext.Default.IReadOnlyListRecordedStepMessage);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
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

/// <summary>Body of <c>POST /api/projects/{project}/move</c>: project-relative paths.</summary>
internal sealed record MoveRequest(string? From, string? To);

internal sealed record FolderRequest(string? Path);

/// <summary>Body of <c>POST /api/projects</c> (ADR-0046): the new project's folder name.</summary>
internal sealed record ProjectRequest(string? Name);

/// <summary>Body of <c>POST /api/validate</c>.</summary>
internal sealed record ValidateRequest(JsonElement? Document);

/// <summary>Body of <c>POST /api/runs</c>.</summary>
internal sealed record StartRunRequest(
    string? Project,
    string? Path,
    JsonElement? Document,
    Dictionary<string, JsonElement>? Arguments,
    int? TimeoutMs,
    Dictionary<string, string?>? ArgumentText = null,
    DebugRunRequest? Debug = null);

/// <summary>The <c>debug</c> part of <c>POST /api/runs</c> (ADR-0040): breakpoints are node ids of the run's workflow.</summary>
internal sealed record DebugRunRequest(List<string?>? Breakpoints, bool PauseAtStart = false);

/// <summary>Body of <c>POST /api/runs/{runId}/debug</c>: continue, stepInto, stepOver, stepOut or pause.</summary>
internal sealed record DebugCommandRequest(string? Command);

/// <summary>Body of <c>PUT /api/runs/{runId}/breakpoints</c>: node ids of the run's workflow.</summary>
internal sealed record BreakpointsRequest(List<string?>? Breakpoints);

/// <summary>Body of <c>POST /api/expressions/scope</c> (ADR-0041): the document being edited and a path in it.</summary>
internal sealed record ExpressionScopeRequest(JsonElement? Document, string? Path);

/// <summary>Body of <c>POST /api/expressions/references</c> (ADR-0041): a name as used at a path of the document.</summary>
internal sealed record ExpressionReferencesRequest(JsonElement? Document, string? Path, string? Name);

/// <summary>Body of <c>POST /api/streams/{streamId}/subscriptions</c>.</summary>
internal sealed record SubscribeRequest(string? RunId, long? AfterSequence, string? RecordingId = null);

/// <summary><c>POST /api/recordings</c> (ADR-0039).</summary>
internal sealed record StartRecordingRequest(string? StartUrl);

/// <summary><c>POST /api/recordings/generate</c>: the steps as the user kept them (ADR-0039).</summary>
internal sealed record GenerateRecordingRequest(string? StartUrl, List<GenerateRecordingStep>? Steps);

/// <summary>One step to generate an activity for.</summary>
internal sealed record GenerateRecordingStep(string? Kind, string? Selector, string? Element, string? Text, bool Secret, List<string>? Values, string? Url, string? FileName);
