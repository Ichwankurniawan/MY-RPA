using System.Net;
using System.Reflection;
using System.Text.Json;

namespace MyRPA.Server.Tests;

/// <summary>The AI Workflow Assistant endpoints (ADR-0047) with the scripted provider fixture: no model or key.</summary>
public sealed class AssistantTests
{
    private const string Workflow = """
        { "schemaVersion": "1.0", "id": "greet", "name": "Greet", "version": "1.0.0",
          "root": { "id": "main", "type": "Core.Sequence", "children": [ { "id": "say", "type": "Core.Log", "properties": { "message": "'hi'" } } ] } }
        """;

    private static readonly string[] _plan = ["Log hi"];
    private static readonly string[] _suggestions = ["Remove the Throw."];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string ScriptedAi { get; } = Path.GetFullPath(
        typeof(AssistantTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ScriptedAiDirectory").Value!);

    private static string Proposal(string workflow) => JsonSerializer.Serialize(new
    {
        kind = "proposal",
        known = Array.Empty<string>(),
        assumptions = Array.Empty<string>(),
        questions = Array.Empty<object>(),
        missing = Array.Empty<string>(),
        plan = _plan,
        summary = "Logs hi.",
        workflowJson = workflow,
    });

    /// <summary>A server whose plugin configuration loads the scripted provider with <paramref name="answers"/>.</summary>
    private static async Task<(ServerHarness Server, string Log)> StartAsync(object[] answers, string? unavailable = null)
    {
        var folder = Directory.CreateTempSubdirectory("myrpa-ai-").FullName;
        var answersFile = Path.Combine(folder, "answers.json");
        var log = Path.Combine(folder, "requests.jsonl");
        File.WriteAllText(answersFile, JsonSerializer.Serialize(answers));
        var settings = new Dictionary<string, string> { ["answers"] = answersFile, ["log"] = log };
        if (unavailable is not null)
        {
            settings["unavailable"] = unavailable;
        }

        var config = Path.Combine(folder, "plugins.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new { pluginConfigVersion = "1.0", plugins = new[] { new { directory = ScriptedAi, settings } } }));
        return (await ServerHarness.StartAsync(o => o with { PluginConfiguration = config }), log);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(ServerHarness h, string path, object body)
    {
        using var response = await h.SendAsync(h.Unsafe(HttpMethod.Post, path, body));
        return (response.StatusCode, await ServerHarness.JsonAsync(response));
    }

    [Fact]
    public async Task WithoutAProvider_TheAssistantSaysWhy_AndAnswersNothing()
    {
        await using var h = await ServerHarness.StartAsync();

        using var info = await h.Client.GetAsync("/api/assistant", Token);
        var (status, body) = await PostAsync(h, "/api/assistant/generate", new { purpose = "create", request = "Greet" });

        var state = await ServerHarness.JsonAsync(info);
        Assert.False(state.GetProperty("available").GetBoolean());
        Assert.Contains("AI provider plugin", state.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("NotConfigured", body.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Generate_ReturnsAValidatedProposal_AndChangesNothingOnDisk()
    {
        var (h, log) = await StartAsync([Proposal(Workflow)]);
        await using var _ = h;
        var before = Directory.GetFileSystemEntries(h.ProjectRoot).Length;

        var (status, body) = await PostAsync(h, "/api/assistant/generate", new { purpose = "create", request = "Log hi" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("proposal", body.GetProperty("outcome").GetString());
        Assert.Equal("greet", JsonDocument.Parse(body.GetProperty("document").GetString()!).RootElement.GetProperty("id").GetString());
        Assert.Equal(["Core.Sequence", "Core.Log"], body.GetProperty("activityTypes").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("scripted", body.GetProperty("usage").GetProperty("provider").GetString());
        Assert.Equal(before, Directory.GetFileSystemEntries(h.ProjectRoot).Length);
        Assert.Single(File.ReadAllLines(log));
    }

    [Fact]
    public async Task Preview_ShowsExactlyWhatWouldBeSent_WithoutSendingIt()
    {
        var (h, log) = await StartAsync([Proposal(Workflow)], unavailable: "Set the key first.");
        await using var _ = h;

        var (status, body) = await PostAsync(h, "/api/assistant/preview", new { operation = "generate", purpose = "edit", request = "Say hello", document = JsonDocument.Parse(Workflow).RootElement });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("MyRPA workflow assistant", body.GetProperty("instructions").GetString(), StringComparison.Ordinal);
        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        Assert.Contains("CURRENT WORKFLOW", messages[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains("Say hello", messages[^1].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(log));
    }

    [Fact]
    public async Task AnUnavailableProvider_IsNotCalled()
    {
        var (h, log) = await StartAsync([Proposal(Workflow)], unavailable: "Set OPENAI_API_KEY for the server.");
        await using var _ = h;

        using var info = await h.Client.GetAsync("/api/assistant", Token);
        var (status, body) = await PostAsync(h, "/api/assistant/explain", new { document = JsonDocument.Parse(Workflow).RootElement });

        Assert.Equal("Set OPENAI_API_KEY for the server.", (await ServerHarness.JsonAsync(info)).GetProperty("reason").GetString());
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "NotConfigured"), (status, body.GetProperty("kind").GetString()));
        Assert.False(File.Exists(log));
    }

    [Theory]
    [InlineData("RateLimited", HttpStatusCode.TooManyRequests)]
    [InlineData("Timeout", HttpStatusCode.GatewayTimeout)]
    [InlineData("Authentication", HttpStatusCode.BadGateway)]
    public async Task ProviderFailures_KeepTheirKind(string kind, HttpStatusCode expected)
    {
        var (h, _) = await StartAsync([new { fail = kind }]);
        await using var __ = h;

        var (status, body) = await PostAsync(h, "/api/assistant/generate", new { purpose = "create", request = "Log hi" });

        Assert.Equal((expected, kind), (status, body.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task Debug_UsesTheRunsOwnError_AndOnlyItsSafeDetails()
    {
        var answer = JsonSerializer.Serialize(new { explanation = "The workflow throws on purpose.", certainty = "likely", nodeId = "fail", suggestions = _suggestions });
        var (h, log) = await StartAsync([answer]);
        await using var _ = h;
        var failing = """{ "schemaVersion": "1.0", "id": "f", "name": "F", "version": "1", "root": { "id": "main", "type": "Core.Sequence", "children": [ { "id": "fail", "type": "Core.Throw", "properties": { "message": "'stock is empty'" } } ] } }""";
        h.WriteWorkflow("fail.json", failing);
        var runId = await h.StartRunAsync("fail.json");
        await h.WaitForRunAsync(runId);

        var (status, body) = await PostAsync(h, "/api/assistant/debug", new { document = JsonDocument.Parse(failing).RootElement, runId });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("likely", "fail", "MYRPA2002"), (body.GetProperty("certainty").GetString(), body.GetProperty("nodeId").GetString(), body.GetProperty("failure").GetProperty("code").GetString()));
        var sent = File.ReadAllText(log);
        Assert.Contains("stock is empty", sent, StringComparison.Ordinal);
        Assert.Contains("MYRPA2002", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BadRequests_AreRefusedWithAReason()
    {
        var (h, _) = await StartAsync([Proposal(Workflow)]);
        await using var __ = h;

        var badPurpose = await PostAsync(h, "/api/assistant/generate", new { purpose = "nope", request = "x" });
        var noDocument = await PostAsync(h, "/api/assistant/generate", new { purpose = "edit", request = "x" });
        var noWords = await PostAsync(h, "/api/assistant/generate", new { purpose = "create", request = "  " });
        var unknownRun = await PostAsync(h, "/api/assistant/debug", new { document = JsonDocument.Parse(Workflow).RootElement, runId = "nope" });

        Assert.Equal(HttpStatusCode.BadRequest, badPurpose.Status);
        Assert.Equal(HttpStatusCode.BadRequest, noDocument.Status);
        Assert.Equal((HttpStatusCode.BadRequest, "Describe what the workflow should do."), (noWords.Status, noWords.Body.GetProperty("error").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, unknownRun.Status);
    }

    [Fact]
    public async Task TheAssistant_NeedsTheAntiForgeryHeader()
    {
        var (h, log) = await StartAsync([Proposal(Workflow)]);
        await using var _ = h;
        using var request = h.Unsafe(HttpMethod.Post, "/api/assistant/generate", new { purpose = "create", request = "Log hi" });
        request.Headers.Remove("X-MyRPA-Request");

        using var response = await h.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(File.Exists(log));
    }
}
