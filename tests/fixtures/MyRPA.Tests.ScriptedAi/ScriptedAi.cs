using System.Text.Json;
using MyRPA.Ai.Contracts;
using MyRPA.Sdk.Plugins;

namespace MyRPA.Tests.ScriptedAi;

/// <summary>Where the answers come from and where requests are recorded.</summary>
public sealed record ScriptedAiOptions(string Answers, string? Log, string Model, string? Unavailable);

/// <summary>
/// Settings: <c>answers</c> (a JSON file: an array whose entries are answer texts, or objects with <c>text</c>,
/// <c>fail</c> (an AiFailureKind) and <c>delayMs</c>), <c>log</c> (a file each request is appended to as one JSON
/// line), <c>model</c>, and <c>unavailable</c> (a reason, to act as an unconfigured provider). The file is read for
/// every request; requests take the entries in order and the last one repeats.
/// </summary>
public sealed class ScriptedAiPlugin : IPlugin
{
    private ScriptedAiOptions? _options;

    public void Initialize(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string? Text(string name) => context.Settings.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
        _options = new ScriptedAiOptions(Text("answers") ?? throw new ArgumentException("Setting 'answers' is required.", nameof(context)), Text("log"), Text("model") ?? "scripted-model", Text("unavailable"));
    }

    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        registrar.AddInstance(_options!).AddService<IAiProvider, ScriptedAiProvider>(PluginServiceLifetime.Plugin);
    }
}

public sealed class ScriptedAiProvider(ScriptedAiOptions options) : IAiProvider
{
    private readonly Lock _gate = new();
    private int _next;

    public AiProviderInfo Info => new("scripted", options.Model, true) { UnavailableReason = options.Unavailable };

    public async Task<AiCompletion> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        int index;
        lock (_gate)
        {
            index = _next++;
            if (options.Log is { } log)
            {
                File.AppendAllText(log, JsonSerializer.Serialize(new
                {
                    instructions = request.Instructions,
                    messages = request.Messages.Select(m => new { role = m.Role.ToString(), text = m.Text }),
                    schema = request.JsonSchema,
                }) + "\n");
            }
        }

        using var answers = JsonDocument.Parse(File.ReadAllText(options.Answers));
        var entry = answers.RootElement[Math.Min(index, answers.RootElement.GetArrayLength() - 1)];
        if (entry.ValueKind == JsonValueKind.String)
        {
            return new AiCompletion(entry.GetString()!, options.Model) { Usage = new AiUsage(100, 50) };
        }

        if (entry.TryGetProperty("delayMs", out var delay))
        {
            await Task.Delay(delay.GetInt32(), cancellationToken).ConfigureAwait(false);
        }

        if (entry.TryGetProperty("fail", out var fail))
        {
            throw new AiProviderException(Enum.Parse<AiFailureKind>(fail.GetString()!), $"Scripted failure: {fail.GetString()}.");
        }

        return new AiCompletion(entry.GetProperty("text").GetString()!, options.Model) { Usage = new AiUsage(100, 50) };
    }
}
