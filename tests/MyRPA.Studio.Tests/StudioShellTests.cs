using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using MyRPA.Architecture.Tests;
using MyRPA.Studio.Services;
using MyRPA.Studio.ViewModels;

namespace MyRPA.Studio.Tests;

/// <summary>The architecture code rules on the compiled WPF shell (ADR-0005, ADR-0008, ADR-0018).</summary>
public sealed class StudioCodeRuleTests
{
    private static readonly Assembly _studio = typeof(StudioComposition).Assembly;

    private static readonly string[] _allowedReferencePrefixes =
    [
        // The engine and Studio logic it composes (the architecture tests' allow-list for MyRPA.Studio).
        "MyRPA.Core", "MyRPA.Workflow", "MyRPA.Activities", "MyRPA.Runtime", "MyRPA.Storage", "MyRPA.Plugins", "MyRPA.Studio.Core",
        "Microsoft.Extensions.", "CommunityToolkit.Mvvm",
        // WPF: allowed in this project only.
        "PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml",
    ];

    [Fact]
    public void NoAsyncVoidMethods() => Assert.Empty(CodeRuleDetectors.FindAsyncVoid(_studio.GetTypes()));

    [Fact]
    public void NoMutableStaticFields() => Assert.Empty(CodeRuleDetectors.FindMutableStatics(_studio.GetTypes()));

    [Fact]
    public void NoBannedApiCalls() => Assert.Empty(CodeRuleDetectors.FindBannedCalls(_studio.GetTypes()));

    [Fact]
    public void CompiledReferences_AreTheEngineStudioCoreHostingAndWpfOnly()
    {
        var unexpected = _studio.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n is not ("System" or "netstandard") && !n.StartsWith("System.", StringComparison.Ordinal))
            .Where(n => !_allowedReferencePrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)));

        Assert.Empty(unexpected);
        Assert.DoesNotContain(_studio.GetReferencedAssemblies(), a => a.Name is "System.Windows.Forms" or "MyRPA.Cli");
    }

    [Fact]
    public void Detectors_StillFindViolations()
    {
        // The linked detectors must work in this project too (a broken link would make the rules above pass silently).
        Assert.NotEmpty(CodeRuleDetectors.FindBannedCalls([typeof(KnownBad)]));
    }

#pragma warning disable CA1822 // intentionally bad code used to verify the linked detector
    private sealed class KnownBad
    {
        public Type? Resolve() => Type.GetType("System.String");
    }
#pragma warning restore CA1822
}

public sealed class StudioCommandLineTests
{
    [Fact]
    public void Parse_ReadsFilePluginsAndConfiguration()
    {
        var parsed = StudioCommandLine.Parse(["wf.json", "--plugin", "a", "--plugin", "b", "--plugin-config", "p.json"], out var error);

        Assert.Null(error);
        Assert.Equal(new StudioCommandLine("wf.json", ["a", "b"], "p.json").WorkflowFile, parsed!.WorkflowFile);
        Assert.Equal(["a", "b"], parsed.PluginDirectories);
        Assert.Equal("p.json", parsed.PluginConfiguration);
    }

    [Theory]
    [InlineData("--plugin")]
    [InlineData("--plugin-config")]
    [InlineData("--plugin-config", "a", "--plugin-config", "b")]
    [InlineData("a.json", "b.json")]
    [InlineData("--verbose")]
    public void Parse_Invalid_ReportsAnError(params string[] args)
    {
        Assert.Null(StudioCommandLine.Parse(args, out var error));
        Assert.NotNull(error);
    }
}

public sealed class StudioCompositionTests
{
    [Fact]
    public void Host_ComposesTheEngineActivitiesAndStudio()
    {
        using var host = StudioComposition.BuildHost(null);

        var catalog = host.Services.GetRequiredService<MyRPA.Core.Activities.IActivityCatalog>();
        Assert.Contains(catalog.Descriptors, d => d.TypeName.Value == "Core.Sequence");
        Assert.IsType<FileWorkflowStorage>(host.Services.GetRequiredService<IWorkflowStorage>());
    }

    [Fact]
    public async Task Plugins_FromTheCommandLine_AreRequired_AndBadDirectoriesFailToLoad()
    {
        var commandLine = StudioCommandLine.Parse(["--plugin", Path.Combine(Path.GetTempPath(), "no-such-plugin-" + Guid.NewGuid().ToString("N"))], out _)!;

        await using var plugins = await StudioComposition.LoadPluginsAsync(commandLine, TestContext.Current.CancellationToken);

        Assert.True(plugins!.HasRequiredFailures);
    }

    [Fact]
    public async Task NoPluginOptions_LoadsNoPlugins()
    {
        Assert.Null(await StudioComposition.LoadPluginsAsync(new StudioCommandLine(null, [], null), TestContext.Current.CancellationToken));
    }
}

/// <summary>Builds the real window (on an STA thread), drives it through the view model and renders it to PNG files.</summary>
public sealed class MainWindowTests
{
    private static string Samples { get; } = FindSamples();

    private static string ScreenshotDirectory => Path.Combine(AppContext.BaseDirectory, "screenshots");

    [Fact]
    public void Window_ShowsASampleWorkflow_AsNestedBlocks()
    {
        Sta.Run(async () =>
        {
            using var host = TestHost();
            var window = host.Services.GetRequiredService<MainWindow>();
            var studio = window.Studio;
            Assert.True(await studio.OpenFileAsync(Path.Combine(Samples, "control-flow.json"), CancellationToken.None));
            ShowOffscreen(window);

            var cards = Visuals<Border>(window).Where(b => b.DataContext is NodeViewModel && DesignerBehaviorsHasSelects(b)).ToList();
            Assert.Equal(studio.Root.DescendantsAndSelf().Count(), cards.Count);
            Assert.Contains(Visuals<TextBlock>(window), t => t.Text == "Core.ForEach");
            Assert.Equal("control-flow.json — MyRPA Studio", window.Title);
            Save(window, "control-flow");
            window.Close();
        });
    }

    [Fact]
    public void Window_SelectingABlock_ShowsItsPropertyEditors()
    {
        Sta.Run(async () =>
        {
            using var host = TestHost();
            var window = host.Services.GetRequiredService<MainWindow>();
            var studio = window.Studio;
            studio.AddActivityCommand.Execute("Core.Log");
            ShowOffscreen(window);

            Assert.Equal("log-1", studio.SelectedNode!.Id);
            var editors = Visuals<TextBox>(window).Where(t => t.DataContext is PropertyEditorViewModel { Name: "message" } && t.IsVisible).ToList();
            var message = Assert.Single(editors);
            Assert.Contains(Visuals<TextBlock>(window), t => t.IsVisible && t.Text.Contains("'message'", StringComparison.Ordinal)); // required, reported

            message.Text = "'Hello from Studio'";
            studio.Properties.Editors.Single(e => e.Name == "message").Commit();
            Flush();

            Assert.Empty(studio.Errors);
            Save(window, "properties");
            await Task.CompletedTask;
            window.Close();
        });
    }

    [Fact]
    public void Window_RunsAWorkflow_AndShowsOutputAndRunStates()
    {
        Sta.Run(async () =>
        {
            using var host = TestHost();
            var window = host.Services.GetRequiredService<MainWindow>();
            var studio = window.Studio;
            Assert.True(await studio.OpenFileAsync(Path.Combine(Samples, "hello-world.json"), CancellationToken.None));
            ShowOffscreen(window);

            await studio.RunCommand.ExecuteAsync(null);
            Flush();

            Assert.Equal("Succeeded", studio.Output.Status);
            Assert.All(studio.Root.DescendantsAndSelf(), n => Assert.Equal(NodeRunState.Completed, n.RunState));
            Assert.NotEmpty(studio.Log.Entries);
            Assert.True(window.OutputTab.IsSelected);
            Save(window, "run");
            window.Close();
        });
    }

    [Fact]
    public void Window_WithUnsavedChanges_AsksBeforeClosing()
    {
        Sta.Run(async () =>
        {
            var dialogs = new ScriptedDialogs { SaveChoice = UnsavedChangesChoice.Cancel };
            using var host = TestHost(dialogs);
            var window = host.Services.GetRequiredService<MainWindow>();
            window.Studio.AddActivityCommand.Execute("Core.Log");
            ShowOffscreen(window);

            window.Close();
            Flush();
            Assert.True(window.IsVisible); // cancelled
            Assert.Equal(1, dialogs.SaveQuestions);

            dialogs.SaveChoice = UnsavedChangesChoice.Discard;
            window.Close();
            Flush();
            Assert.False(window.IsVisible);
            await Task.CompletedTask;
        });
    }

    // Regression (Phase 5 review): a value typed into a field is committed on Enter or when focus leaves the field.
    // Ctrl+S, F5 and closing the window do neither; each must still see the typed value. The field keeps focus below.
    [Fact]
    public void CtrlS_WithFocusStillInAnEditedField_SavesTheTypedValue()
    {
        Sta.Run(async () =>
        {
            var file = Path.Combine(Path.GetTempPath(), "myrpa-pending-" + Guid.NewGuid().ToString("N") + ".json");
            var dialogs = new ScriptedDialogs { SavePath = file };
            using var host = TestHost(dialogs);
            var window = host.Services.GetRequiredService<MainWindow>();
            window.Studio.AddActivityCommand.Execute("Core.Log");
            ShowOffscreen(window);
            var message = TypeInto(window, t => t.DataContext is PropertyEditorViewModel { Name: "message" }, "'typed, then Ctrl+S'");
            Assert.Null(window.Studio.Document.Root.Children[0].Property("message")); // not committed yet

            try
            {
                await ExecuteKeyBinding(window, new KeyGesture(Key.S, ModifierKeys.Control));

                Assert.Contains("'typed, then Ctrl+S'", File.ReadAllText(file), StringComparison.Ordinal);
                Assert.False(window.Studio.IsDirty);
                Assert.Equal("'typed, then Ctrl+S'", message.Text);
            }
            finally
            {
                File.Delete(file);
            }

            window.Close();
        });
    }

    [Fact]
    public void F5_WithFocusStillInAnEditedField_RunsTheTypedValue()
    {
        Sta.Run(async () =>
        {
            using var host = TestHost();
            var window = host.Services.GetRequiredService<MainWindow>();
            window.Studio.AddActivityCommand.Execute("Core.Log");
            ShowOffscreen(window);
            TypeInto(window, t => t.DataContext is PropertyEditorViewModel { Name: "message" }, "'typed, then F5'");

            await ExecuteKeyBinding(window, new KeyGesture(Key.F5));
            Flush();

            Assert.Equal("Succeeded", window.Studio.Output.Status);
            Assert.Contains(window.Studio.Log.Entries, e => e.Message == "typed, then F5");
            window.Close();
        });
    }

    [Fact]
    public void Close_WithFocusStillInAnEditedField_AsksToSaveTheTypedValue()
    {
        Sta.Run(async () =>
        {
            var dialogs = new ScriptedDialogs { SaveChoice = UnsavedChangesChoice.Cancel };
            using var host = TestHost(dialogs);
            var window = host.Services.GetRequiredService<MainWindow>();
            ShowOffscreen(window);
            Assert.False(window.Studio.IsDirty);
            TypeInto(window, t => AutomationProperties.GetName(t) == "Workflow name", "Typed, then closed");

            window.Close();
            Flush();

            Assert.Equal(1, dialogs.SaveQuestions);
            Assert.True(window.IsVisible); // cancelled: the typed value is still there to save
            Assert.Equal("Typed, then closed", window.Studio.Document.Name);

            dialogs.SaveChoice = UnsavedChangesChoice.Discard;
            window.Close();
            Flush();
            Assert.False(window.IsVisible);
            await Task.CompletedTask;
        });
    }

    // Types into a visible text box the way a user does: the text reaches the view model on every keystroke, focus stays.
    private static TextBox TypeInto(Window window, Func<TextBox, bool> match, string text)
    {
        var box = Visuals<TextBox>(window).Single(t => t.IsVisible && match(t));
        box.Focus();
        box.Text = text;
        Flush();
        return box;
    }

    // Runs the command bound to a gesture in the window's input bindings (what pressing the keys does).
    private static async Task ExecuteKeyBinding(Window window, KeyGesture gesture)
    {
        var binding = window.InputBindings.OfType<KeyBinding>().Single(b => b.Key == gesture.Key && b.Modifiers == gesture.Modifiers);
        Assert.True(binding.Command.CanExecute(binding.CommandParameter));
        if (binding.Command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand command)
        {
            await command.ExecuteAsync(binding.CommandParameter);
        }
        else
        {
            binding.Command.Execute(binding.CommandParameter);
        }
    }

    // Window tests must never show real dialogs or touch the real clipboard: a message box on the developer's desktop
    // blocks the test until someone clicks it.
    private static Microsoft.Extensions.Hosting.IHost TestHost(ScriptedDialogs? dialogs = null)
    {
        var host = StudioComposition.BuildHost(null, s => s
            .AddSingleton<IStudioDialogs>(dialogs ?? new ScriptedDialogs())
            .AddSingleton<IStudioClipboard, MemoryClipboard>());
        Assert.IsType<ScriptedDialogs>(host.Services.GetRequiredService<IStudioDialogs>());
        return host;
    }

    private static bool DesignerBehaviorsHasSelects(DependencyObject element) => Designer.DesignerBehaviors.GetSelects(element) is not null;

    private static void ShowOffscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        Flush();
    }

    private static void Flush()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> Visuals<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Visuals<T>(child))
            {
                yield return nested;
            }
        }
    }

    // Screenshots are written next to the test binaries for manual inspection (they are not compared).
    private static void Save(Window window, string name)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(ScreenshotDirectory);
        using var file = File.Create(Path.Combine(ScreenshotDirectory, name + ".png"));
        encoder.Save(file);
    }

    private static string FindSamples()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MyRPA.sln")))
            {
                return Path.Combine(dir.FullName, "samples");
            }
        }

        throw new InvalidOperationException("MyRPA.sln not found.");
    }
}

/// <summary>Runs a test body on a new STA thread with a WPF dispatcher (async continuations stay on that thread).</summary>
internal static class Sta
{
    public static void Run(Func<Task> body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await body();
                }
#pragma warning disable CA1031 // rethrown on the test thread
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    frame.Continue = false;
                }
            });
            Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}

internal sealed class MemoryClipboard : IStudioClipboard
{
    private string? _text;

    public string? GetText() => _text;

    public void SetText(string text) => _text = text;
}

internal sealed class ScriptedDialogs : IStudioDialogs
{
    public UnsavedChangesChoice SaveChoice { get; set; } = UnsavedChangesChoice.Discard;

    public int SaveQuestions { get; private set; }

    public List<string> Errors { get; } = [];

    public string? ChooseFileToOpen() => null;

    public string? SavePath { get; set; }

    public string? ChooseFileToSave(string suggestedName) => SavePath;

    public UnsavedChangesChoice AskToSaveChanges(string documentName)
    {
        SaveQuestions++;
        return SaveChoice;
    }

    public IReadOnlyDictionary<string, string>? AskForArguments(IReadOnlyList<ArgumentPrompt> arguments) => new Dictionary<string, string>();

    public string? AskForText(string title, string prompt, string initialValue) => null;

    public void ShowError(string title, string message) => Errors.Add(message);
}
