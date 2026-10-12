using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyRPA.Activities;
using MyRPA.Plugins;
using MyRPA.Runtime;
using MyRPA.Storage;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Validation;

namespace MyRPA.Ai.Agent.Tests;

/// <summary>
/// The agent plugin and the scripted provider loaded through the real plugin host, the engine with built-ins and the
/// file resolver (so workflow tools resolve next to the workflow), and a log capture of the execution log.
/// </summary>
public sealed class AgentHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    private AgentHost(PluginSet plugins, ServiceProvider services, string folder, LogCapture log)
    {
        Plugins = plugins;
        _services = services;
        Folder = folder;
        Log = log;
    }

    public PluginSet Plugins { get; }

    public IServiceProvider Services => _services;

    public string Folder { get; }

    public LogCapture Log { get; }

    public string RequestsFile => Path.Combine(Folder, "requests.jsonl");

    public static string Metadata(string key) => Path.GetFullPath(
        typeof(AgentHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!);

    /// <summary>Starts a host whose model answers <paramref name="answers"/> in order (the last repeats).</summary>
    public static async Task<AgentHost> StartAsync(object[] answers, IReadOnlyDictionary<string, string>? agentSettings = null, string? unavailable = null, bool withProvider = true)
    {
        var folder = Directory.CreateTempSubdirectory("myrpa-agent-").FullName;
        var options = new PluginHostOptions();
        if (withProvider)
        {
            File.WriteAllText(Path.Combine(folder, "answers.json"), JsonSerializer.Serialize(answers));
            var scripted = new PluginSource { Directory = Metadata("ScriptedAiDirectory") };
            scripted.Settings["answers"] = Path.Combine(folder, "answers.json");
            scripted.Settings["log"] = Path.Combine(folder, "requests.jsonl");
            if (unavailable is not null)
            {
                scripted.Settings["unavailable"] = unavailable;
            }

            options.Sources.Add(scripted);
        }

        var agent = new PluginSource { Directory = Metadata("AgentPluginDirectory") };
        foreach (var (name, value) in agentSettings ?? new Dictionary<string, string>())
        {
            agent.Settings[name] = value;
        }

        options.Sources.Add(agent);
        var plugins = await PluginLoader.LoadAsync(options, TestContext.Current.CancellationToken);
        Assert.False(plugins.HasRequiredFailures, string.Join(Environment.NewLine, plugins.Diagnostics));
        var log = new LogCapture();
        var services = new ServiceCollection()
            .AddLogging(b => b.AddProvider(log).SetMinimumLevel(LogLevel.Information))
            .AddMyRpaRuntime()
            .AddMyRpaActivities()
            .AddMyRpaStorage()
            .AddMyRpaPlugins(plugins)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        return new AgentHost(plugins, services, folder, log);
    }

    public string Write(string relativePath, string json)
    {
        var path = Path.Combine(Folder, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Runs <paramref name="json"/> saved as main.json in the host's folder.</summary>
    public async Task<WorkflowExecutionResult> RunAsync(string json, CancellationToken? cancellationToken = null)
    {
        var path = Write("main.json", json);
        var load = _services.GetRequiredService<WorkflowLoader>().Load(json);
        Assert.True(load.IsValid, string.Join(Environment.NewLine, load.Diagnostics));
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkflowRunner>().RunAsync(load.Workflow!, new WorkflowRunRequest { Location = path }, cancellationToken ?? TestContext.Current.CancellationToken);
    }

    /// <summary>The requests the scripted provider received, as JSON.</summary>
    public IReadOnlyList<JsonElement> Requests() =>
        File.Exists(RequestsFile) ? [.. File.ReadAllLines(RequestsFile).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone())] : [];

    /// <summary>Everything the model was sent, as one text.</summary>
    public string SentText() => File.Exists(RequestsFile) ? File.ReadAllText(RequestsFile) : string.Empty;

    public WorkflowLoader Loader => _services.GetRequiredService<WorkflowLoader>();

    public MyRPA.Core.Activities.IActivityCatalog Catalog => _services.GetRequiredService<MyRPA.Core.Activities.IActivityCatalog>();

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await Plugins.DisposeAsync();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // A stdio server may still hold its folder for a moment; the temp folder is cleaned later.
        }
    }
}

/// <summary>Keeps the execution log's messages (category MyRPA.Workflow.Log) with their node ids.</summary>
public sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<(string Message, string? NodeId)> Entries { get; } = new();

    public IEnumerable<string> Messages => Entries.Select(e => e.Message);

    public ILogger CreateLogger(string categoryName) => categoryName == "MyRPA.Workflow.Log" ? new Logger(this) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose()
    {
    }

    private sealed class Logger(LogCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var node = state is IEnumerable<KeyValuePair<string, object?>> pairs ? pairs.FirstOrDefault(p => p.Key == "NodeId").Value as string : null;
            owner.Entries.Enqueue((formatter(state, exception), node));
        }
    }
}

/// <summary>Workflows and scripted answers.</summary>
public static class Script
{
    public static string Call(string tool, object arguments) =>
        JsonSerializer.Serialize(new { action = "call_tool", tool, arguments = JsonSerializer.Serialize(arguments), output = string.Empty });

    public static string Finish(object output) =>
        JsonSerializer.Serialize(new { action = "finish", tool = string.Empty, arguments = string.Empty, output = JsonSerializer.Serialize(output) });

    public const string InvoiceSchema = """{"type":"object","required":["customer","total","creditLimit"],"properties":{"customer":{"type":"string"},"total":{"type":"number"},"creditLimit":{"type":"number"}},"additionalProperties":false}""";

    public const string CreditTool = """[ { "workflow": "tools/credit.json", "name": "credit_limit", "description": "Looks up a customer's credit limit.", "input": { "customer": "String" } } ]""";

    /// <summary>A workflow tool: In customer, Out limit (500 for ACME, else 0); logs that it ran.</summary>
    public const string CreditWorkflow = """
        { "schemaVersion": "1.0", "id": "credit", "name": "Credit", "version": "1",
          "arguments": [ { "name": "customer", "direction": "In", "type": "String" }, { "name": "limit", "direction": "Out", "type": "Int" } ],
          "root": { "id": "main", "type": "Core.Sequence", "children": [
            { "id": "ran", "type": "Core.Log", "properties": { "message": "'credit tool ran for ' + customer" } },
            { "id": "set", "type": "Core.Assign", "properties": { "to": "limit", "value": "500" } } ] } }
        """;

    /// <summary>A workflow with one AI.Agent node (id "agent") whose result goes to Out "out" and tool calls to Out "calls".</summary>
    public static string AgentWorkflow(string tools = "", string schema = "", string extra = "", string task = "invoice") => $$"""
        { "schemaVersion": "1.0", "id": "main", "name": "Main", "version": "1",
          "arguments": [ { "name": "out", "direction": "Out", "type": "Object" }, { "name": "calls", "direction": "Out", "type": "List" } ],
          "variables": [ { "name": "invoice", "type": "String", "default": "Invoice INV-7 from ACME, total 120.50" } ],
          "root": { "id": "main", "type": "Core.Sequence", "children": [
            { "id": "agent", "type": "AI.Agent", "properties": {
                "instructions": "Extract the invoice and look up the customer's credit limit.",
                "task": {{JsonSerializer.Serialize(task)}},
                "tools": {{JsonSerializer.Serialize(tools)}},
                "outputSchema": {{JsonSerializer.Serialize(schema)}},
                "result": "out", "toolCalls": "calls"{{extra}} } } ] } }
        """;
}
