using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using MyRPA.Core.Activities;
using MyRPA.Sdk.Automation;
using MyRPA.Sdk.Plugins;
using MyRPA.Tests.FixtureDependency;
using MyRPA.Workflow.Execution;
using MyRPA.Workflow.Values;

namespace MyRPA.Tests.FixturePlugin;

/// <summary>Records lifecycle events so tests can observe them through the engine or by reflection.</summary>
public sealed class FixtureJournal
{
    private readonly ConcurrentQueue<string> _events = new();
    private int _runs;

    public IReadOnlyList<string> Events => [.. _events];

    public void Add(string entry) => _events.Enqueue(entry);

    public int NextRun() => Interlocked.Increment(ref _runs);
}

/// <summary>The well-behaved fixture plugin: activities, a provider, a run-lifetime service and a settings echo.</summary>
public sealed class FixturePlugin : IPlugin, IAsyncDisposable
{
    public FixtureJournal Journal { get; } = new();

    public void Initialize(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var mode = context.Settings.TryGetValue("mode", out var value) ? value : "default";
        Journal.Add($"plugin:initialize:{context.Plugin.Id}:{mode}:{context.HostSdkVersion}");
    }

    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        registrar
            .AddInstance(Journal)
            .AddProvider<IFixtureProvider, FixtureProvider>(FixtureProvider.Id)
            .AddService<FixtureRunState, FixtureRunState>(PluginServiceLifetime.Run)
            .AddActivity<DependencyActivity>(DependencyActivity.Descriptor)
            .AddActivity<RunStateActivity>(RunStateActivity.Descriptor)
            .AddActivity<JournalActivity>(JournalActivity.Descriptor);
        Journal.Add("plugin:register");
    }

    public ValueTask DisposeAsync()
    {
        Journal.Add("plugin:disposed");
        return ValueTask.CompletedTask;
    }
}

public interface IFixtureProvider : IAutomationProvider
{
    string Describe();
}

public sealed class FixtureProvider : IFixtureProvider, IDisposable
{
    public static AutomationProviderId Id { get; } = new("Fixture.Provider");

    private readonly FixtureJournal _journal;

    public FixtureProvider(FixtureJournal journal)
    {
        _journal = journal;
        _journal.Add("provider:created");
    }

    public AutomationProviderDescriptor Descriptor { get; } = new(Id, "Fixture", "Test");

    public string Describe() => Descriptor.DisplayName;

    public void Dispose() => _journal.Add("provider:disposed");
}

/// <summary>Run-lifetime service: one per top-level run, disposed when the run ends.</summary>
public sealed class FixtureRunState : IDisposable
{
    private readonly FixtureJournal _journal;

    public FixtureRunState(FixtureJournal journal)
    {
        _journal = journal;
        Id = journal.NextRun().ToString(CultureInfo.InvariantCulture);
        _journal.Add($"run:created:{Id}");
    }

    public string Id { get; }

    public void Dispose() => _journal.Add($"run:disposed:{Id}");
}

/// <summary><c>Fixture.Dependency</c>: returns the load context of the plugin-private dependency.</summary>
public sealed class DependencyActivity : IActivity
{
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Fixture.Dependency"), "Dependency", "Fixture", properties: [new("to", ActivityPropertyKind.AssignmentTarget, isRequired: true)]);

    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.SetValue(context.GetName("to"), FixtureText.LoadContextName());
        return ActivityResult.CompletedTask;
    }
}

/// <summary><c>Fixture.RunState</c>: returns the id of the run-lifetime service instance.</summary>
public sealed class RunStateActivity(FixtureRunState state) : IActivity
{
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Fixture.RunState"), "Run state", "Fixture", properties: [new("to", ActivityPropertyKind.AssignmentTarget, isRequired: true)]);

    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.SetValue(context.GetName("to"), state.Id);
        return ActivityResult.CompletedTask;
    }
}

/// <summary><c>Fixture.Journal</c>: returns the journal (and touches the provider so it is created).</summary>
public sealed class JournalActivity(FixtureJournal journal, IFixtureProvider provider, ILogger<JournalActivity> logger) : IActivity
{
    public static ActivityDescriptor Descriptor { get; } = new(
        new ActivityTypeName("Fixture.Journal"), "Journal", "Fixture", properties: [new("to", ActivityPropertyKind.AssignmentTarget, isRequired: true)]);

    public ValueTask<ActivityResult> ExecuteAsync(IActivityContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
#pragma warning disable CA1848, CA1873 // Fixture code.
        logger.LogInformation("Journal read via {Provider}", provider.Describe());
#pragma warning restore CA1848, CA1873
        context.SetValue(context.GetName("to"), WorkflowValues.List(journal.Events));
        return ActivityResult.CompletedTask;
    }
}

public sealed class ThrowingInitializePlugin : IPlugin
{
    public void Initialize(PluginContext context) => throw new InvalidOperationException("boom in Initialize");

    public void Register(IPluginRegistrar registrar) => throw new InvalidOperationException("must not be called");
}

public sealed class ThrowingRegisterPlugin : IPlugin
{
    public void Initialize(PluginContext context)
    {
    }

    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        registrar.AddActivity<DependencyActivity>(DependencyActivity.Descriptor);
        throw new InvalidOperationException("boom in Register");
    }
}

/// <summary>Registers an activity its manifest does not declare.</summary>
public sealed class UndeclaredActivityPlugin : IPlugin
{
    public void Initialize(PluginContext context)
    {
    }

    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        registrar.AddActivity<DependencyActivity>(DependencyActivity.Descriptor);
    }
}

/// <summary>Tries to replace a host service.</summary>
public sealed class HostServicePlugin : IPlugin
{
    public void Initialize(PluginContext context)
    {
    }

    public void Register(IPluginRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(registrar);
        registrar.AddService<ILoggerFactory, HijackingLoggerFactory>(PluginServiceLifetime.Plugin);
    }
}

public sealed class HijackingLoggerFactory : ILoggerFactory
{
    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => throw new NotSupportedException();

    public void Dispose()
    {
    }
}

/// <summary>Registers nothing (for dependency-graph tests).</summary>
public sealed class EmptyPlugin : IPlugin
{
    public void Initialize(PluginContext context)
    {
    }

    public void Register(IPluginRegistrar registrar)
    {
    }
}

/// <summary>Not an <see cref="IPlugin"/>.</summary>
public sealed class NotAPlugin
{
}

/// <summary>Has no public parameterless constructor.</summary>
public sealed class NoDefaultConstructorPlugin(string name) : IPlugin
{
    public string Name { get; } = name;

    public void Initialize(PluginContext context)
    {
    }

    public void Register(IPluginRegistrar registrar)
    {
    }
}
