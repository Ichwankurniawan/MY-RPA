using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyRPA.Orchestration;
using MyRPA.Plugins;
using MyRPA.Server;

namespace MyRPA.Runner.Tests;

/// <summary>Paths from the build (fixture and plugin outputs, samples).</summary>
internal static class Paths
{
    public static string Of(string key) => Path.GetFullPath(
        typeof(Paths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!);

    /// <summary>A plugin configuration with the scripted AI provider (the agent demo's answers) and the AI Agent plugin.</summary>
    public static string AgentPluginConfig(string folder)
    {
        var answers = Path.Combine(Of("SamplesDirectory"), "plugins", "agent-demo", "answers.json");
        var path = Path.Combine(folder, "agent-plugins.json");
        File.WriteAllText(path, $$"""
            { "pluginConfigVersion": "1.0", "plugins": [
              { "directory": {{JsonSerializer.Serialize(Of("ScriptedAiDirectory"))}}, "settings": { "answers": {{JsonSerializer.Serialize(answers)}} } },
              { "directory": {{JsonSerializer.Serialize(Of("AgentPluginDirectory"))}} } ] }
            """);
        return path;
    }
}

/// <summary>
/// A real Server with the orchestrator and a Runner listener, and Runners started from the Runner's own composition
/// root. Management calls use an administrator access token (the token path of ADR-0051).
/// </summary>
internal sealed class Stack : IAsyncDisposable
{
    private readonly List<(IHost Host, PluginSet? Plugins)> _runners = [];
    private readonly bool _ownsFolder;

    private Stack(RunningServer server, PluginSet? plugins, string folder, bool ownsFolder, string adminToken)
    {
        Server = server;
        ServerPlugins = plugins;
        Folder = folder;
        _ownsFolder = ownsFolder;
        Admin = Client(adminToken);
    }

    public RunningServer Server { get; }

    public PluginSet? ServerPlugins { get; }

    public string Folder { get; }

    public string ProjectRoot => Path.Combine(Folder, "project");

    public HttpClient Admin { get; }

    public Orchestrator Orchestrator => Server.App.Services.GetRequiredService<Orchestrator>();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<Stack> StartAsync(string? pluginConfig = null, string? folder = null)
    {
        var owns = folder is null;
        folder ??= Path.Combine(Path.GetTempPath(), "myrpa-stack-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(folder, "project"));
        var options = new ServerOptions
        {
            Projects = [new ProjectRoot("project", Path.Combine(folder, "project"))],
            Port = 0,
            DataDirectory = Path.Combine(folder, "data"),
            RunnerUrl = new Uri("http://127.0.0.1:0/"),
            PluginConfiguration = pluginConfig,
            ConfigureOrchestrator = o =>
            {
                o.HeartbeatInterval = TimeSpan.FromMilliseconds(200);
                o.LeaseDuration = TimeSpan.FromSeconds(3);
            },
            OrchestratorTick = TimeSpan.FromMilliseconds(100),
        };
        var plugins = await ServerApplication.LoadPluginsAsync(options, Ct);
        var server = await ServerApplication.StartAsync(options, plugins, Ct);
        var orchestrator = server.App.Services.GetRequiredService<Orchestrator>();
        var existing = (await orchestrator.Store.ListAccessTokensAsync(Ct)).Count;
        var (_, token) = await orchestrator.CreateAccessTokenAsync(Actor.LocalOwner, $"tests-{existing}", Role.Admin, Ct);
        return new Stack(server, plugins, folder, owns, token);
    }

    public HttpClient Client(string? bearer, Uri? baseUri = null)
    {
        var client = new HttpClient { BaseAddress = baseUri ?? Server.BaseUri };
        if (bearer is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return client;
    }

    public void Write(string relative, string json)
    {
        var full = Path.Combine(ProjectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, json);
    }

    /// <summary>Publishes and deploys a project workflow; returns the process id.</summary>
    public async Task<string> DeployAsync(string path)
    {
        var published = await PostAsync(Admin, "/api/orchestrator/publish", new { project = "project", path });
        var processId = published.GetProperty("process").GetProperty("processId").GetString()!;
        var version = published.GetProperty("version").GetProperty("version").GetInt32();
        await PostAsync(Admin, $"/api/orchestrator/processes/{processId}/deploy", new { version });
        return processId;
    }

    public async Task<string> SubmitAsync(string processId, object? argumentText = null)
    {
        var submitted = await PostAsync(Admin, "/api/orchestrator/jobs", new { processId, argumentText });
        return submitted.GetProperty("job").GetProperty("jobId").GetString()!;
    }

    public async Task<(string RunnerId, string Token)> CreateRunnerAsync(string name, string? group = null)
    {
        var created = await PostAsync(Admin, "/api/orchestrator/runners", new { name, group, maxConcurrency = 1 });
        return (created.GetProperty("runnerId").GetString()!, created.GetProperty("token").GetString()!);
    }

    /// <summary>Starts a Runner with the given token (and plugins), connected to the Runner listener.</summary>
    public async Task<IHost> StartRunnerAsync(string token, string? pluginConfig = null, string name = "runner")
    {
        var options = new RunnerOptions
        {
            ServerUrl = Server.RunnerUri!,
            Token = token,
            PluginConfiguration = pluginConfig,
            WorkDirectory = Path.Combine(Folder, "work-" + name),
            ClaimWait = TimeSpan.FromSeconds(2),
            ShutdownGrace = TimeSpan.FromSeconds(10),
        };
        PluginSet? plugins = null;
        if (pluginConfig is not null)
        {
            plugins = await PluginLoader.LoadAsync(await PluginConfigurationFile.CreateHostOptionsAsync([], pluginConfig, Ct), Ct);
            Assert.False(plugins.HasRequiredFailures, string.Join(Environment.NewLine, plugins.Diagnostics));
        }

        var host = RunnerApplication.BuildHost(options, plugins);
        await host.StartAsync(Ct);
        _runners.Add((host, plugins));
        return host;
    }

    public static async Task<JsonElement> PostAsync(HttpClient client, string path, object body, int expected = 0)
    {
        using var response = await client.PostAsJsonAsync(path, body, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(expected == 0 ? response.IsSuccessStatusCode : (int)response.StatusCode == expected, $"{path}: {(int)response.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Polls a job until its state is one of <paramref name="states"/> (or fails after a timeout).</summary>
    public async Task<JsonElement> WaitForJobAsync(string jobId, params string[] states)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        JsonElement detail = default;
        while (DateTimeOffset.UtcNow < deadline)
        {
            detail = await GetAsync(Admin, $"/api/orchestrator/jobs/{jobId}");
            if (states.Contains(detail.GetProperty("job").GetProperty("state").GetString()))
            {
                return detail;
            }

            await Task.Delay(100, Ct);
        }

        Assert.Fail($"Job {jobId} did not reach {string.Join("/", states)}: {detail}");
        return detail;
    }

    public async Task StopRunnersAsync()
    {
        foreach (var (host, plugins) in _runners)
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
            if (plugins is not null)
            {
                await plugins.DisposeAsync();
            }
        }

        _runners.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopRunnersAsync();
        Admin.Dispose();
        await Server.DisposeAsync();
        if (_ownsFolder)
        {
            TryDelete(Folder);
        }
    }

    public static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a temporary folder.
        }
    }
}

/// <summary>Workflows for the end-to-end tests.</summary>
internal static class Flows
{
    public const string Greet = """
        { "schemaVersion": "1.0", "id": "greet", "name": "Greet", "version": "1.0.0",
          "arguments": [
            { "name": "who", "direction": "In", "type": "String", "default": "World" },
            { "name": "greeting", "direction": "Out", "type": "String" } ],
          "root": { "id": "main", "type": "Core.Sequence", "children": [
            { "id": "assign", "type": "Core.Assign", "properties": { "to": "greeting", "value": "'Hello, ' + who" } },
            { "id": "log", "type": "Core.Log", "properties": { "message": "greeting" } } ] } }
        """;

    public const string Wait = """
        { "schemaVersion": "1.0", "id": "wait", "name": "Wait", "version": "1.0.0",
          "root": { "id": "main", "type": "Core.Sequence", "children": [
            { "id": "log", "type": "Core.Log", "properties": { "message": "'waiting'" } },
            { "id": "delay", "type": "Core.Delay", "properties": { "milliseconds": "60000" } } ] } }
        """;
}
