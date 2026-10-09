using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace MyRPA.Server.Tests;

/// <summary>Debug runs through the server (ADR-0040): start with breakpoints, commands, values only from the debug endpoint.</summary>
public sealed class DebugTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Workflow(string children) =>
        $$"""{ "schemaVersion": "1.0", "id": "dbg", "name": "Debug", "version": "1.0.0", "variables": [ { "name": "n", "type": "Int", "default": 0 } ], "root": { "id": "main", "type": "Core.Sequence", "children": [ {{children}} ] } }""";

    private static string Log(string id) => $$"""{ "id": "{{id}}", "type": "Core.Log", "properties": { "message": "'{{id}}'" } }""";

    private static async Task<string> StartDebugAsync(ServerHarness h, string path, object debug)
    {
        using var response = await h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/runs", new { project = h.ProjectName, path, debug }));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await ServerHarness.JsonAsync(response)).GetProperty("runId").GetString()!;
    }

    private static async Task<(SseReader Sse, string RunId)> FollowAsync(ServerHarness h, string path, object debug)
    {
        var streamId = await h.OpenStreamAsync();
        var sse = await SseReader.OpenAsync(h.Client, streamId);
        var runId = await StartDebugAsync(h, path, debug);
        await h.SubscribeAsync(streamId, runId);
        return (sse, runId);
    }

    private static async Task<JsonElement> NextPauseAsync(SseReader sse) =>
        (await sse.ReadUntilAsync(e => e.Count > 0 && e[^1].Kind == "debug.paused"))[^1].Data;

    private static async Task<HttpStatusCode> CommandAsync(ServerHarness h, string runId, string command)
    {
        using var response = await h.SendAsync(h.Unsafe(HttpMethod.Post, $"/api/runs/{runId}/debug", new { command }));
        return response.StatusCode;
    }

    private static async Task<JsonElement> DebugStateAsync(ServerHarness h, string runId)
    {
        using var response = await h.Client.GetAsync($"/api/runs/{runId}/debug", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ServerHarness.JsonAsync(response);
    }

    private static (string?, string?) Describe(JsonElement paused) => (paused.GetProperty("nodeId").GetString(), paused.GetProperty("reason").GetString());

    [Fact]
    public async Task Breakpoint_Pauses_TheStreamCarriesNoValues_TheDebugEndpointDoes()
    {
        await using var h = await ServerHarness.StartAsync();
        h.WriteWorkflow("dbg.json", Workflow($$"""{ "id": "set", "type": "Core.Assign", "properties": { "to": "n", "value": "41" } }, {{Log("say")}}"""));
        var (reader, runId) = await FollowAsync(h, "dbg.json", new { breakpoints = new List<string> { "say" } });
        await using var sse = reader;

        var paused = await NextPauseAsync(sse);
        Assert.Equal(("say", "breakpoint"), Describe(paused));
        Assert.Equal("dbg", paused.GetProperty("workflowId").GetString());
        Assert.False(paused.TryGetProperty("values", out _));
        Assert.False(paused.TryGetProperty("message", out _));

        var state = await DebugStateAsync(h, runId);
        var at = state.GetProperty("paused");
        Assert.Equal(("say", "breakpoint"), Describe(at));
        Assert.Equal("Core.Log", at.GetProperty("activityType").GetString());
        var value = Assert.Single(at.GetProperty("values").EnumerateArray());
        Assert.Equal(("n", "Variable", "Int", 41), (value.GetProperty("name").GetString(), value.GetProperty("kind").GetString(), value.GetProperty("type").GetString(), value.GetProperty("value").GetInt32()));
        Assert.Equal(["say"], state.GetProperty("breakpoints").EnumerateArray().Select(b => b.GetString()));

        Assert.Equal(HttpStatusCode.Accepted, await CommandAsync(h, runId, "continue"));
        Assert.Equal("Succeeded", (await h.WaitForRunAsync(runId)).GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, (await DebugStateAsync(h, runId)).GetProperty("paused").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, await CommandAsync(h, runId, "continue"));
    }

    [Fact]
    public async Task StartPaused_StepInto_ThenBreakpointsChangedWhileRunning()
    {
        await using var h = await ServerHarness.StartAsync();
        h.WriteWorkflow("dbg.json", Workflow($"{Log("a")}, {Log("b")}, {Log("c")}"));
        var (reader, runId) = await FollowAsync(h, "dbg.json", new { breakpoints = new List<string> { "b" }, pauseAtStart = true });
        await using var sse = reader;

        Assert.Equal(("main", "pause"), Describe(await NextPauseAsync(sse)));
        Assert.Equal(HttpStatusCode.Accepted, await CommandAsync(h, runId, "stepInto"));
        Assert.Equal(("a", "step"), Describe(await NextPauseAsync(sse)));

        using var replace = await h.SendAsync(h.Unsafe(HttpMethod.Put, $"/api/runs/{runId}/breakpoints", new { breakpoints = new List<string> { "c" } }));
        Assert.Equal(HttpStatusCode.NoContent, replace.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, await CommandAsync(h, runId, "continue"));
        Assert.Equal(("c", "breakpoint"), Describe(await NextPauseAsync(sse)));

        Assert.Equal(HttpStatusCode.Accepted, await CommandAsync(h, runId, "stepOver"));
        Assert.Equal("Succeeded", (await h.WaitForRunAsync(runId)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Pause_WhileRunning_PausesBeforeTheNextNode_AndCancelStopsIt()
    {
        await using var h = await ServerHarness.StartAsync();
        h.WriteWorkflow("dbg.json", Workflow($$"""{ "id": "wait", "type": "Core.Delay", "properties": { "milliseconds": 300 } }, {{Log("after")}}"""));
        var (reader, runId) = await FollowAsync(h, "dbg.json", new { });
        await using var sse = reader;
        await sse.ReadUntilAsync(e => e.Any(x => x.Kind == "node.started" && x.Data.GetProperty("nodeId").GetString() == "wait"));

        Assert.Equal(HttpStatusCode.Accepted, await CommandAsync(h, runId, "pause"));
        Assert.Equal(("after", "pause"), Describe(await NextPauseAsync(sse)));
        Assert.Equal(HttpStatusCode.Conflict, await CommandAsync(h, runId, "pause"));

        using var cancel = await h.SendAsync(h.Unsafe(HttpMethod.Post, $"/api/runs/{runId}/cancel"));
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        Assert.Equal("Cancelled", (await h.WaitForRunAsync(runId)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task DebugEndpoints_RefuseBadInput_OtherRuns_AndOtherSessions()
    {
        await using var h = await ServerHarness.StartAsync();
        h.WriteWorkflow("dbg.json", Workflow(Log("a")));
        var plain = await h.StartRunAsync("dbg.json");
        var debugged = await StartDebugAsync(h, "dbg.json", new { pauseAtStart = true });

        using var badBreakpoint = await h.SendAsync(h.Unsafe(HttpMethod.Post, "/api/runs", rawJson: $$"""{ "project": "{{h.ProjectName}}", "path": "dbg.json", "debug": { "breakpoints": [ "" ] } }"""));
        using var notDebugged = await h.Client.GetAsync($"/api/runs/{plain}/debug", Token);
        using var unknown = await h.Client.GetAsync("/api/runs/nope/debug", Token);
        using var withoutHeader = new HttpRequestMessage(HttpMethod.Post, $"/api/runs/{debugged}/debug") { Content = JsonContent.Create(new { command = "continue" }) };
        using var refused = await h.Client.SendAsync(withoutHeader, Token);
        using var stranger = ServerHarness.NewClient(h.BaseUri);
        using var noSession = await stranger.GetAsync($"/api/runs/{debugged}/debug", Token);

        Assert.Equal(HttpStatusCode.BadRequest, badBreakpoint.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, await CommandAsync(h, debugged, "jump"));
        Assert.Equal(HttpStatusCode.NotFound, notDebugged.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.False(refused.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
        Assert.Null(h.Server.App.Services.GetRequiredService<DebugRuns>().Find(debugged, "another-session"));

        Assert.Equal(HttpStatusCode.Accepted, await CommandAsync(h, debugged, "continue"));
        Assert.Equal("Succeeded", (await h.WaitForRunAsync(debugged)).GetProperty("status").GetString());
    }
}
