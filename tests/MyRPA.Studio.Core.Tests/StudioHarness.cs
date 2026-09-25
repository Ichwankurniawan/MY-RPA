using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MyRPA.Activities;
using MyRPA.Core.Activities;
using MyRPA.Core.Identifiers;
using MyRPA.Runtime;
using MyRPA.Storage;
using MyRPA.Studio.Running;
using MyRPA.Studio.Services;
using MyRPA.Studio.ViewModels;
using MyRPA.Workflow.Execution;

namespace MyRPA.Studio.Tests;

/// <summary>A headless Studio: the real engine and built-in activities, a fake clock, scripted dialogs.</summary>
public sealed class StudioHarness : IDisposable
{
    private readonly ServiceProvider _services;

    public StudioHarness()
    {
        Feed = new StudioLogFeed(Time);
        _services = new ServiceCollection()
            .AddLogging(b => b.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(Feed))
            .AddSingleton<TimeProvider>(Time)
            .AddMyRpaRuntime()
            .AddMyRpaActivities()
            .AddMyRpaStorage()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        ViewModel = new StudioViewModel(
            _services.GetRequiredService<IActivityCatalog>(),
            _services.GetRequiredService<IWorkflowRunner>(),
            _services.GetRequiredService<IIdGenerator>(),
            Dialogs,
            Clipboard,
            Dispatcher,
            Storage,
            Feed);
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));

    public StudioLogFeed Feed { get; }

    public ScriptedDialogs Dialogs { get; } = new();

    public MemoryClipboard Clipboard { get; } = new();

    public InlineDispatcher Dispatcher { get; } = new();

    public MemoryStorage Storage { get; } = new();

    public StudioViewModel ViewModel { get; }

    public void Dispose()
    {
        ViewModel.Dispose();
        _services.Dispose();
    }
}

public sealed class ScriptedDialogs : IStudioDialogs
{
    public string? OpenPath { get; set; }

    public string? SavePath { get; set; }

    public UnsavedChangesChoice SaveChoice { get; set; } = UnsavedChangesChoice.Cancel;

    public IReadOnlyDictionary<string, string>? ArgumentAnswers { get; set; } = new Dictionary<string, string>();

    public IReadOnlyList<ArgumentPrompt> LastPrompts { get; private set; } = [];

    public string? TextAnswer { get; set; }

    public List<string> Errors { get; } = [];

    public int SaveQuestions { get; private set; }

    public string? ChooseFileToOpen() => OpenPath;

    public string? ChooseFileToSave(string suggestedName) => SavePath;

    public UnsavedChangesChoice AskToSaveChanges(string documentName)
    {
        SaveQuestions++;
        return SaveChoice;
    }

    public IReadOnlyDictionary<string, string>? AskForArguments(IReadOnlyList<ArgumentPrompt> arguments)
    {
        LastPrompts = arguments;
        return ArgumentAnswers;
    }

    public string? AskForText(string title, string prompt, string initialValue) => TextAnswer;

    public void ShowError(string title, string message) => Errors.Add($"{title}: {message}");
}

public sealed class MemoryClipboard : IStudioClipboard
{
    public string? Text { get; set; }

    public string? GetText() => Text;

    public void SetText(string text) => Text = text;
}

/// <summary>Runs posted actions immediately, on the posting thread (the engine runs one node at a time).</summary>
public sealed class InlineDispatcher : IUiDispatcher
{
    private readonly Lock _gate = new();

    public void Post(Action action)
    {
        lock (_gate)
        {
            action();
        }
    }
}

public sealed class MemoryStorage : IWorkflowStorage
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<string> ReadAsync(string location, CancellationToken cancellationToken) =>
        Files.TryGetValue(Path.GetFullPath(location), out var json) ? Task.FromResult(json) : Task.FromException<string>(new FileNotFoundException("Not found.", location));

    public Task WriteAsync(string location, string json, CancellationToken cancellationToken)
    {
        Files[Path.GetFullPath(location)] = json;
        return Task.CompletedTask;
    }
}
