using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace MyRPA.Server.Tests;

/// <summary>Local-mode security (ADR-0025).</summary>
public sealed class SecurityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Server_ListensOnLoopbackOnly()
    {
        await using var h = await ServerHarness.StartAsync(signIn: false);

        Assert.Equal("127.0.0.1", h.BaseUri.Host);
    }

    [Fact]
    public async Task Api_WithoutASession_IsUnauthorized()
    {
        await using var h = await ServerHarness.StartAsync(signIn: false);

        using var response = await h.Client.GetAsync("/api/info", Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task StartLink_SetsAStrictHttpOnlySessionCookie_AndWorksOnlyOnce()
    {
        await using var h = await ServerHarness.StartAsync(signIn: false);
        var start = h.StartUri!;

        using var first = await h.Client.GetAsync(start.PathAndQuery, Token);
        var cookie = Assert.Single(first.Headers.GetValues("Set-Cookie"));
        using var info = await h.Client.GetAsync("/api/info", Token);
        using var other = ServerHarness.NewClient(h.BaseUri);
        using var reused = await other.GetAsync(start.PathAndQuery, Token);

        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal("/", first.Headers.Location?.OriginalString);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, reused.StatusCode);
        Assert.Null(h.StartUri);
    }

    [Fact]
    public async Task WrongStartToken_IsRefused()
    {
        await using var h = await ServerHarness.StartAsync(signIn: false);

        using var response = await h.Client.GetAsync("/?token=guess", Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(h.StartUri); // the real token is still unused
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("attacker.test:80")]
    [InlineData("127.0.0.1:1")]
    public async Task ForeignHostHeader_IsRejected_AgainstDnsRebinding(string host)
    {
        await using var h = await ServerHarness.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/info");
        request.Headers.Host = host;

        using var response = await h.Client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LocalhostHostHeader_IsAccepted()
    {
        await using var h = await ServerHarness.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/info");
        request.Headers.Host = $"localhost:{h.BaseUri.Port}";

        using var response = await h.Client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CrossOriginOrUnmarkedStateChanges_AreForbidden()
    {
        await using var h = await ServerHarness.StartAsync();

        using var crossOrigin = h.Unsafe(HttpMethod.Post, "/api/streams");
        crossOrigin.Headers.Remove("Origin");
        crossOrigin.Headers.Add("Origin", "http://evil.example");
        using var noHeader = new HttpRequestMessage(HttpMethod.Post, "/api/streams");
        noHeader.Headers.Add("Origin", h.BaseUri.GetLeftPart(UriPartial.Authority));
        using var noOrigin = h.Unsafe(HttpMethod.Post, "/api/streams");
        noOrigin.Headers.Remove("Origin");

        Assert.Equal(HttpStatusCode.Forbidden, (await h.SendAsync(crossOrigin)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.SendAsync(noHeader)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.SendAsync(noOrigin)).StatusCode);
    }

    [Fact]
    public async Task NonJsonBodies_AreRejected()
    {
        await using var h = await ServerHarness.StartAsync();
        using var request = h.Unsafe(HttpMethod.Post, "/api/validate");
        request.Content = new StringContent("document=1", Encoding.UTF8, "application/x-www-form-urlencoded");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await h.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Responses_CarrySecurityHeaders_AndNoCorsHeaders()
    {
        await using var h = await ServerHarness.StartAsync();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/runs");
        preflight.Headers.Add("Origin", "http://evil.example");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");

        using var info = await h.Client.GetAsync("/api/info", Token);
        using var preflightResponse = await h.Client.SendAsync(preflight, Token);

        Assert.Contains("default-src 'self'", info.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", info.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("nosniff", info.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-store", info.Headers.CacheControl?.ToString());
        Assert.False(info.Headers.Contains("Server"));
        Assert.DoesNotContain(preflightResponse.Headers, header => header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("a/../../outside.json")]
    [InlineData("..%2Foutside.json")]
    [InlineData("%2E%2E/outside.json")]
    [InlineData("C:%2FWindows%2Fwin.json")]
    [InlineData("a%5C..%5C..%5Coutside.json")]
    [InlineData(".hidden/secret.json")]
    [InlineData("notes.txt")]
    public async Task FilePaths_CannotLeaveTheProject_OrReadOtherFiles(string path)
    {
        await using var h = await ServerHarness.StartAsync();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(h.ProjectRoot)!, "outside.json"), "{}");

        using var response = await h.Client.GetAsync($"/api/projects/{h.ProjectName}/workflows/{path}", Token);

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"{path}: {response.StatusCode}");
    }
}

/// <summary>Command line, catalog, plugins and project files.</summary>
public sealed class ProjectAndCatalogTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string SamplePlugin { get; } = Path.GetFullPath(
        typeof(ProjectAndCatalogTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "SamplePluginDirectory").Value!);

    [Fact]
    public void CommandLine_RequiresProjects_AndParsesOptions()
    {
        var folder = Directory.CreateTempSubdirectory("myrpa-cli-");
        try
        {
            Assert.Null(ServerCommandLine.Parse([], out var noProject));
            Assert.Contains("--project", noProject, StringComparison.Ordinal);
            Assert.Null(ServerCommandLine.Parse(["--project", Path.Combine(folder.FullName, "missing")], out _));
            Assert.Null(ServerCommandLine.Parse(["--port", "70000", "--project", folder.FullName], out _));
            Assert.Null(ServerCommandLine.Parse(["--project", folder.FullName, "--project", folder.FullName], out _));

            Assert.Null(ServerCommandLine.Parse(["--project", folder.FullName, "--web", folder.FullName], out var noIndex));
            Assert.Contains("index.html", noIndex, StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(folder.FullName, "index.html"), "<!doctype html>");

            var options = ServerCommandLine.Parse(["--project", folder.FullName, "--port", "0", "--plugin", "p", "--plugin-config", "c.json", "--web", folder.FullName], out var error);

            Assert.Null(error);
            Assert.Equal(folder.Name, Assert.Single(options!.Projects).Name);
            Assert.Equal(0, options.Port);
            Assert.Equal(["p"], options.PluginDirectories);
            Assert.Equal("c.json", options.PluginConfiguration);
            Assert.Equal(folder.FullName, options.WebRoot);
            Assert.Null(ServerCommandLine.Parse(["--project", folder.FullName, "--web", folder.FullName, "--web", folder.FullName], out _));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Info_AndCatalog_DescribeTheServer()
    {
        await using var h = await ServerHarness.StartAsync();

        var info = await ServerHarness.JsonAsync(await h.Client.GetAsync("/api/info", Token));
        var catalog = await ServerHarness.JsonAsync(await h.Client.GetAsync("/api/activities", Token));

        Assert.Equal("local", info.GetProperty("mode").GetString());
        Assert.Equal(["1.0"], info.GetProperty("workflowSchemaVersions").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(h.ProjectName, info.GetProperty("projects")[0].GetString());
        Assert.Equal("1.0", catalog.GetProperty("catalogVersion").GetString());
        Assert.Contains(catalog.GetProperty("activities").EnumerateArray(), a => a.GetProperty("type").GetString() == "Core.Log");
    }

    [Fact]
    public async Task Plugins_AreLoadedThroughThePluginHost_AndAttributed()
    {
        await using var h = await ServerHarness.StartAsync(o => o with { PluginDirectories = [SamplePlugin] });

        var plugins = await ServerHarness.JsonAsync(await h.Client.GetAsync("/api/plugins", Token));
        var catalog = await ServerHarness.JsonAsync(await h.Client.GetAsync("/api/activities", Token));

        var plugin = Assert.Single(plugins.GetProperty("plugins").EnumerateArray());
        Assert.Equal("MyRPA.Samples.Demo", plugin.GetProperty("id").GetString());
        Assert.Matches("^[0-9a-f]{64}$", plugin.GetProperty("sha256").GetString());
        Assert.Contains(plugin.GetProperty("activities").EnumerateArray(), a => a.GetString() == "Demo.Echo");
        Assert.Contains(catalog.GetProperty("activities").EnumerateArray(), a => a.GetProperty("type").GetString() == "Demo.Echo");
    }

    [Fact]
    public async Task Files_AreListedReadCreatedUpdatedAndDeleted_WithETags()
    {
        await using var h = await ServerHarness.StartAsync();
        h.WriteWorkflow("flows/main.json", ServerHarness.Logs(1));
        Directory.CreateDirectory(Path.Combine(h.ProjectRoot, "node_modules"));
        File.WriteAllText(Path.Combine(h.ProjectRoot, "node_modules", "skip.json"), "{}");
        var files = $"/api/projects/{h.ProjectName}/workflows";

        var list = await ServerHarness.JsonAsync(await h.Client.GetAsync(files, Token));
        using var read = await h.Client.GetAsync($"{files}/flows/main.json", Token);
        var etag = read.Headers.ETag!.Tag;

        using var create = h.Unsafe(HttpMethod.Put, $"{files}/new.json", rawJson: ServerHarness.Logs(2));
        create.Headers.Add("If-None-Match", "*");
        using var created = await h.SendAsync(create);
        using var createAgain = h.Unsafe(HttpMethod.Put, $"{files}/new.json", rawJson: ServerHarness.Logs(2));
        createAgain.Headers.Add("If-None-Match", "*");
        using var noPrecondition = h.Unsafe(HttpMethod.Put, $"{files}/flows/main.json", rawJson: ServerHarness.Logs(3));
        using var stale = h.Unsafe(HttpMethod.Put, $"{files}/flows/main.json", rawJson: ServerHarness.Logs(3));
        stale.Headers.TryAddWithoutValidation("If-Match", "\"stale\"");
        using var update = h.Unsafe(HttpMethod.Put, $"{files}/flows/main.json", rawJson: ServerHarness.Logs(3));
        update.Headers.TryAddWithoutValidation("If-Match", etag);
        using var notJson = h.Unsafe(HttpMethod.Put, $"{files}/bad.json", rawJson: "[1, 2]");
        notJson.Headers.Add("If-None-Match", "*");

        Assert.Equal(["flows/main.json"], list.GetProperty("workflows").EnumerateArray().Select(f => f.GetProperty("path").GetString()));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await h.SendAsync(createAgain)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await h.SendAsync(noPrecondition)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await h.SendAsync(stale)).StatusCode);
        using var updated = await h.SendAsync(update);
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.SendAsync(notJson)).StatusCode);
        Assert.Equal(ServerHarness.Logs(3), File.ReadAllText(Path.Combine(h.ProjectRoot, "flows", "main.json")));

        using var delete = h.Unsafe(HttpMethod.Delete, $"{files}/flows/main.json");
        delete.Headers.TryAddWithoutValidation("If-Match", updated.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NoContent, (await h.SendAsync(delete)).StatusCode);
        Assert.False(File.Exists(Path.Combine(h.ProjectRoot, "flows", "main.json")));
    }

    [Fact]
    public async Task Validate_UsesTheEngineLoader_AndLocatesMissingPropertiesOnTheProperty()
    {
        await using var h = await ServerHarness.StartAsync();
        var document = JsonDocument.Parse(ServerHarness.Workflow("""{ "id": "say", "type": "Core.Log", "properties": {} }""")).RootElement;

        using var response = await h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/validate", new { document }));
        var result = await ServerHarness.JsonAsync(response);

        Assert.False(result.GetProperty("valid").GetBoolean());
        var diagnostic = Assert.Single(result.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("MYRPA1040", diagnostic.GetProperty("code").GetString());
        Assert.Equal("$.root.properties.message", diagnostic.GetProperty("path").GetString());
        Assert.Equal("say", diagnostic.GetProperty("nodeId").GetString());
    }
}

/// <summary>Runs and the multiplexed event stream (ADR-0024).</summary>
public sealed class RunAndStreamTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Run_FromAProjectFile_WithArguments_ReportsItsResultAndOutputs()
    {
        await using var h = await ServerHarness.StartAsync();
        h.WriteWorkflow("greet.json", ServerHarness.Workflow(
            """{ "id": "set", "type": "Core.Assign", "properties": { "to": "greeting", "value": "'Hello, ' + who" } }""",
            """[ { "name": "who", "direction": "In", "type": "String", "required": true }, { "name": "greeting", "direction": "Out", "type": "String" } ]"""));

        var runId = await h.StartRunAsync("greet.json", arguments: new { who = "Ada" });
        var result = await h.WaitForRunAsync(runId);

        Assert.Equal("Succeeded", result.GetProperty("status").GetString());
        Assert.Equal("Hello, Ada", result.GetProperty("outputs").GetProperty("greeting").GetString());
        Assert.Equal(runId, result.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Run_OfAnUnsavedDocument_ResolvesSubWorkflowsFromItsPath()
    {
        await using var h = await ServerHarness.StartAsync();
        h.WriteWorkflow("lib/child.json", ServerHarness.Workflow("""{ "id": "inner", "type": "Core.Log", "properties": { "message": "'from child'" } }""", id: "child"));
        var document = JsonDocument.Parse(ServerHarness.Workflow("""{ "id": "call", "type": "Core.InvokeWorkflow", "properties": { "workflow": "lib/child.json" } }""", id: "draft")).RootElement;

        var runId = await h.StartRunAsync("draft.json", document: document);

        Assert.Equal("Succeeded", (await h.WaitForRunAsync(runId)).GetProperty("status").GetString());
        Assert.False(File.Exists(Path.Combine(h.ProjectRoot, "draft.json"))); // running never saves
    }

    [Fact]
    public async Task InvalidWorkflow_IsNotStarted_AndReturnsDiagnostics()
    {
        await using var h = await ServerHarness.StartAsync();
        var document = JsonDocument.Parse(ServerHarness.Workflow("""{ "id": "say", "type": "Core.Log" }""")).RootElement;

        using var response = await h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/runs", new { project = h.ProjectName, path = "x.json", document }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("MYRPA1040", (await ServerHarness.JsonAsync(response)).GetProperty("diagnostics")[0].GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("""{ "nobody": "x" }""", "not an input argument")]
    [InlineData("""{ "n": "many" }""", "Argument 'n'")]
    public async Task BadArguments_AreRejected(string arguments, string expected)
    {
        await using var h = await ServerHarness.StartAsync();
        h.WriteWorkflow("n.json", ServerHarness.Workflow("""{ "id": "main", "type": "Core.Sequence" }""", """[ { "name": "n", "direction": "In", "type": "Int" } ]"""));

        using var response = await h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/runs", rawJson: $$"""{ "project": "{{h.ProjectName}}", "path": "n.json", "arguments": {{arguments}} }"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, (await ServerHarness.JsonAsync(response)).GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneStream_MultiplexesSeveralRuns_EachInOrderAndComplete()
    {
        await using var h = await ServerHarness.StartAsync();
        h.WriteWorkflow("a.json", ServerHarness.Logs(3, "a"));
        h.WriteWorkflow("b.json", ServerHarness.Logs(5, "b"));
        var streamId = await h.OpenStreamAsync();
        await using var sse = await SseReader.OpenAsync(h.Client, streamId);
        Assert.Equal("stream.opened", (await sse.NextAsync()).Kind);

        var a = await h.StartRunAsync("a.json");
        var b = await h.StartRunAsync("b.json");
        await h.SubscribeAsync(streamId, a);
        await h.SubscribeAsync(streamId, b);
        var events = await sse.ReadUntilAsync(e => e.Any(x => x.IsRunCompletion(a)) && e.Any(x => x.IsRunCompletion(b)));

        foreach (var (run, logs) in new[] { (a, 3), (b, 5) })
        {
            var mine = events.Where(e => e.RunId == run).ToList();
            Assert.Equal(Enumerable.Range(1, mine.Count).Select(i => (long)i), mine.Select(e => e.Sequence));
            Assert.Equal(logs, mine.Count(e => e.Kind == "log"));
            Assert.Equal("execution.started", mine[0].Kind);
        }

        // The id is the stream's position vector: subscription index = last sequence delivered.
        Assert.Matches("^0=\\d+,1=\\d+$", events[^1].Id);
    }

    [Fact]
    public async Task Reconnect_WithLastEventId_ResumesEveryRun_WithoutGapsOrDuplicates()
    {
        await using var h = await ServerHarness.StartAsync();
        h.WriteWorkflow("a.json", ServerHarness.Logs(40, "a"));
        h.WriteWorkflow("b.json", ServerHarness.Logs(40, "b"));
        var a = await h.StartRunAsync("a.json");
        var b = await h.StartRunAsync("b.json");
        await h.WaitForRunAsync(a);
        await h.WaitForRunAsync(b);
        var streamId = await h.OpenStreamAsync();
        await h.SubscribeAsync(streamId, a);
        await h.SubscribeAsync(streamId, b);

        List<SseEvent> first;
        await using (var sse = await SseReader.OpenAsync(h.Client, streamId))
        {
            first = await sse.ReadUntilAsync(e => e.Count >= 51); // stream.opened + 50 run events
        }

        List<SseEvent> rest;
        await using (var resumed = await SseReader.OpenAsync(h.Client, streamId, lastEventId: first[^1].Id))
        {
            rest = await resumed.ReadUntilAsync(e => e.Any(x => x.IsRunCompletion(a)) && e.Any(x => x.IsRunCompletion(b)));
        }

        var all = first.Concat(rest).Where(e => e.RunId is not null).ToList();
        foreach (var run in new[] { a, b })
        {
            var sequences = all.Where(e => e.RunId == run).Select(e => e.Sequence).ToList();
            Assert.Equal(Enumerable.Range(1, sequences.Count).Select(i => (long)i), sequences);
        }
    }

    [Fact]
    public async Task SubscribingAfterASequence_SkipsWhatTheClientAlreadyHas()
    {
        await using var h = await ServerHarness.StartAsync();
        h.WriteWorkflow("a.json", ServerHarness.Logs(3, "a"));
        var a = await h.StartRunAsync("a.json");
        await h.WaitForRunAsync(a);
        var streamId = await h.OpenStreamAsync();
        await h.SubscribeAsync(streamId, a, afterSequence: 5);

        await using var sse = await SseReader.OpenAsync(h.Client, streamId);
        var events = await sse.ReadUntilAsync(e => e.Any(x => x.IsRunCompletion(a)));

        Assert.Equal(6, events.First(e => e.RunId == a).Sequence);
    }

    [Fact]
    public async Task GapsFromTheBoundedReplayBuffer_ArePassedThrough()
    {
        await using var h = await ServerHarness.StartAsync(o => o with { ConfigureHosting = x => x.EventBufferCapacity = 8 });
        h.WriteWorkflow("a.json", ServerHarness.Logs(20, "a"));
        var a = await h.StartRunAsync("a.json");
        await h.WaitForRunAsync(a);
        var streamId = await h.OpenStreamAsync();
        await h.SubscribeAsync(streamId, a);

        await using var sse = await SseReader.OpenAsync(h.Client, streamId);
        var events = await sse.ReadUntilAsync(e => e.Any(x => x.IsRunCompletion(a)));

        var gap = Assert.Single(events, e => e.Kind == "stream.gap");
        Assert.Equal(1, gap.Data.GetProperty("missingFromSequence").GetInt64());
        Assert.Equal(56, gap.Data.GetProperty("missingToSequence").GetInt64());
        Assert.Equal(8, events.Count(e => e.RunId == a && e.Sequence > 0));
    }

    [Fact]
    public async Task Cancel_EndsARunningRun_AndTheStreamReportsIt()
    {
        await using var h = await ServerHarness.StartAsync();
        h.WriteWorkflow("wait.json", ServerHarness.Workflow("""{ "id": "wait", "type": "Core.Delay", "properties": { "milliseconds": 60000 } }"""));
        var streamId = await h.OpenStreamAsync();
        await using var sse = await SseReader.OpenAsync(h.Client, streamId);
        var run = await h.StartRunAsync("wait.json");
        await h.SubscribeAsync(streamId, run);
        await sse.ReadUntilAsync(e => e.Any(x => x.Kind == "node.started"));

        using var cancel = await h.SendAsync(h.Unsafe(HttpMethod.Post, $"/api/runs/{run}/cancel"));
        var events = await sse.ReadUntilAsync(e => e.Any(x => x.IsRunCompletion(run)));
        using var again = await h.SendAsync(h.Unsafe(HttpMethod.Post, $"/api/runs/{run}/cancel"));

        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        Assert.Equal("Cancelled", events.Single(e => e.IsRunCompletion(run)).Data.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Unsubscribe_StopsARunsEvents_OthersContinue()
    {
        await using var h = await ServerHarness.StartAsync();
        h.WriteWorkflow("wait.json", ServerHarness.Workflow("""{ "id": "wait", "type": "Core.Delay", "properties": { "milliseconds": 60000 } }"""));
        h.WriteWorkflow("a.json", ServerHarness.Logs(2, "a"));
        var streamId = await h.OpenStreamAsync();
        await using var sse = await SseReader.OpenAsync(h.Client, streamId);
        var slow = await h.StartRunAsync("wait.json");
        await h.SubscribeAsync(streamId, slow);
        await sse.ReadUntilAsync(e => e.Any(x => x.RunId == slow && x.Kind == "node.started"));

        using var unsubscribe = await h.SendAsync(h.Unsafe(HttpMethod.Delete, $"/api/streams/{streamId}/subscriptions/{slow}"));
        await h.SendAsync(h.Unsafe(HttpMethod.Post, $"/api/runs/{slow}/cancel"));
        var a = await h.StartRunAsync("a.json");
        await h.SubscribeAsync(streamId, a);
        var events = await sse.ReadUntilAsync(e => e.Any(x => x.IsRunCompletion(a)));

        Assert.Equal(HttpStatusCode.NoContent, unsubscribe.StatusCode);
        Assert.DoesNotContain(events, e => e.RunId == slow);
    }

    [Fact]
    public async Task Streams_BelongToTheirSession_AndUnknownRunsCannotBeSubscribed()
    {
        await using var h = await ServerHarness.StartAsync();
        var streamId = await h.OpenStreamAsync();
        using var stranger = ServerHarness.NewClient(h.BaseUri);

        using var withoutSession = await stranger.GetAsync($"/api/streams/{streamId}", Token);
        using var unknownRun = await h.SendAsync(h.Unsafe(HttpMethod.Post, $"/api/streams/{streamId}/subscriptions", new { runId = "nope" }));
        using var unknownStream = await h.Client.GetAsync("/api/streams/nope", Token);

        Assert.Equal(HttpStatusCode.Unauthorized, withoutSession.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownRun.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownStream.StatusCode);
    }

    [Fact]
    public async Task ConcurrentRuns_OnOneStream_NeverMixEvents()
    {
        await using var h = await ServerHarness.StartAsync();
        h.WriteWorkflow("tag.json", ServerHarness.Workflow(
            """{ "id": "say", "type": "Core.Log", "properties": { "message": "'run ' + tag" } }""",
            """[ { "name": "tag", "direction": "In", "type": "String", "required": true } ]"""));
        var streamId = await h.OpenStreamAsync();
        await using var sse = await SseReader.OpenAsync(h.Client, streamId);

        var runs = new Dictionary<string, string>();
        for (var i = 0; i < 12; i++)
        {
            var run = await h.StartRunAsync("tag.json", arguments: new { tag = $"t{i}" });
            runs[run] = $"run t{i}";
            await h.SubscribeAsync(streamId, run);
        }

        var events = await sse.ReadUntilAsync(e => runs.Keys.All(r => e.Any(x => x.IsRunCompletion(r))));

        foreach (var (run, message) in runs)
        {
            Assert.Equal(message, Assert.Single(events, e => e.RunId == run && e.Kind == "log").Data.GetProperty("message").GetString());
        }
    }
}

/// <summary>The built Web Studio served from the server's own origin (ADR-0022, ADR-0025).</summary>
public sealed class WebStudioHostingTests : IDisposable
{
    private readonly DirectoryInfo _web = Directory.CreateTempSubdirectory("myrpa-web-");

    public WebStudioHostingTests()
    {
        File.WriteAllText(Path.Combine(_web.FullName, "index.html"), "<!doctype html><title>MyRPA Studio</title>");
        Directory.CreateDirectory(Path.Combine(_web.FullName, "assets"));
        File.WriteAllText(Path.Combine(_web.FullName, "assets", "app.js"), "export {};");
        File.WriteAllText(Path.Combine(_web.FullName, ".env"), "SECRET=1");
        File.WriteAllText(Path.Combine(_web.FullName, "notes.unknownext"), "x");
        File.WriteAllText(Path.Combine(_web.Parent!.FullName, _web.Name + "-outside.json"), "{}");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        File.Delete(Path.Combine(_web.Parent!.FullName, _web.Name + "-outside.json"));
        _web.Delete(recursive: true);
    }

    [Fact]
    public async Task StartLink_OpensTheStudio_WithTheSecurityHeaders()
    {
        await using var h = await ServerHarness.StartAsync(o => o with { WebRoot = _web.FullName }, signIn: false);
        await h.SignInAsync(h.Client);

        using var response = await h.Client.GetAsync("/", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("MyRPA Studio", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.StartsWith("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Assets_AreServed_WithTheSameGuard_ButApiStillNeedsASession()
    {
        await using var h = await ServerHarness.StartAsync(o => o with { WebRoot = _web.FullName }, signIn: false);

        using var asset = await h.Client.GetAsync("/assets/app.js", Token);
        using var api = await h.Client.GetAsync("/api/info", Token);
        using var foreignHost = new HttpRequestMessage(HttpMethod.Get, "/assets/app.js");
        foreignHost.Headers.Host = "attacker.example";
        using var rebound = await h.Client.SendAsync(foreignHost, Token);

        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("text/javascript", asset.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", asset.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rebound.StatusCode);
    }

    [Theory]
    [InlineData("/.env")]
    [InlineData("/notes.unknownext")]
    [InlineData("/%2e%2e/outside.json")]
    [InlineData("/assets/%2e%2e/%2e%2e/outside.json")]
    public async Task HiddenUnknownOrOutsideFiles_AreNotServed(string path)
    {
        await using var h = await ServerHarness.StartAsync(o => o with { WebRoot = _web.FullName });

        using var response = await h.Client.GetAsync(path.Replace("outside.json", _web.Name + "-outside.json", StringComparison.Ordinal), Token);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WithoutAWebRoot_TheRootServesNoUi()
    {
        await using var h = await ServerHarness.StartAsync();

        using var root = await h.Client.GetAsync("/", Token);
        using var asset = await h.Client.GetAsync("/assets/app.js", Token);

        Assert.Equal("text/plain", root.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, asset.StatusCode);
    }
}
