using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using MyRPA.Activities;
using MyRPA.Core.Activities;
using MyRPA.Plugins.Manifest;
using MyRPA.Sdk.Automation;
using MyRPA.Sdk.Plugins;
using MyRPA.Workflow.Execution;

namespace MyRPA.Plugins.Loading;

/// <summary>
/// Stages a plugin's registrations (ADR-0014). Nothing reaches the host until <see cref="Validate"/> accepts the whole
/// set; <see cref="Apply"/> then adds it to the host's service collection.
/// </summary>
internal sealed class PluginRegistrar(PluginInfo plugin, PluginManifest manifest, AssemblyLoadContext context) : IPluginRegistrar
{
    private readonly List<ActivityRegistration> _activities = [];
    private readonly List<(AutomationProviderId Id, Type Service, Type Implementation)> _providers = [];
    private readonly List<ServiceDescriptor> _services = [];
    private readonly List<string> _problems = [];

    public PluginInfo Plugin { get; } = plugin;

    public IReadOnlyList<ActivityRegistration> Activities => _activities;

    /// <summary>The service types under which the plugin's providers are registered.</summary>
    public IReadOnlyList<Type> ProviderServiceTypes => [.. _providers.Select(p => p.Service)];

    public IPluginRegistrar AddActivity<TActivity>(ActivityDescriptor descriptor)
        where TActivity : class, IActivity
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        RequireOwned(typeof(TActivity), $"Activity '{descriptor.TypeName}'");
        try
        {
            _activities.Add(new ActivityRegistration(descriptor, typeof(TActivity), manifest.Id.Value));
        }
        catch (ArgumentException ex)
        {
            _problems.Add($"Activity '{descriptor.TypeName}': {ex.Message}");
        }

        return this;
    }

    public IPluginRegistrar AddProvider<TService, TProvider>(AutomationProviderId providerId)
        where TService : class, IAutomationProvider
        where TProvider : class, TService
    {
        ArgumentNullException.ThrowIfNull(providerId);
        RequireOwned(typeof(TService), $"Provider '{providerId}' service type");
        RequireOwned(typeof(TProvider), $"Provider '{providerId}' implementation");
        RequireConcrete(typeof(TProvider), $"Provider '{providerId}' implementation");
        _providers.Add((providerId, typeof(TService), typeof(TProvider)));
        return this;
    }

    public IPluginRegistrar AddService<TService, TImplementation>(PluginServiceLifetime lifetime)
        where TService : class
        where TImplementation : class, TService
    {
        RequireOwned(typeof(TService), "Service type");
        RequireOwned(typeof(TImplementation), "Service implementation");
        RequireConcrete(typeof(TImplementation), "Service implementation");
        var serviceLifetime = lifetime switch
        {
            PluginServiceLifetime.Plugin => ServiceLifetime.Singleton,
            PluginServiceLifetime.Run => ServiceLifetime.Scoped,
            _ => (ServiceLifetime?)null,
        };

        if (serviceLifetime is null)
        {
            _problems.Add($"Service '{typeof(TService).Name}' has an unknown lifetime '{lifetime}'.");
        }
        else
        {
            AddServiceDescriptor(ServiceDescriptor.Describe(typeof(TService), typeof(TImplementation), serviceLifetime.Value));
        }

        return this;
    }

    public IPluginRegistrar AddInstance<TService>(TService instance)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        RequireOwned(typeof(TService), "Instance service type");
        AddServiceDescriptor(ServiceDescriptor.Singleton(instance));
        return this;
    }

    /// <summary>Returns every problem with the staged registrations (empty when they can be applied).</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>(_problems);

        var registeredActivities = _activities.Select(a => a.Descriptor.TypeName).ToList();
        problems.AddRange(registeredActivities.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => $"Activity '{g.Key}' is registered more than once."));
        problems.AddRange(registeredActivities.Distinct().Except(manifest.Activities).Select(n => $"Activity '{n}' is registered but not declared in the manifest."));
        problems.AddRange(manifest.Activities.Except(registeredActivities).Select(n => $"Activity '{n}' is declared in the manifest but not registered."));

        var registeredProviders = _providers.Select(p => p.Id).ToList();
        problems.AddRange(registeredProviders.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => $"Provider '{g.Key}' is registered more than once."));
        problems.AddRange(registeredProviders.Distinct().Except(manifest.Providers).Select(n => $"Provider '{n}' is registered but not declared in the manifest."));
        problems.AddRange(manifest.Providers.Except(registeredProviders).Select(n => $"Provider '{n}' is declared in the manifest but not registered."));

        var serviceTypes = _services.Select(s => s.ServiceType).Concat(_providers.Select(p => p.Service)).ToList();
        problems.AddRange(serviceTypes.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => $"Service type '{g.Key.Name}' is registered more than once."));
        return problems;
    }

    /// <summary>Adds the validated registrations to the host's services.</summary>
    public void Apply(IServiceCollection services)
    {
        foreach (var activity in _activities)
        {
            services.AddSingleton(activity);
        }

        foreach (var (id, service, implementation) in _providers)
        {
            // One registration per provider, so the container creates and disposes exactly one instance.
            services.AddSingleton(service, sp => CreateProvider(sp, id, implementation));
        }

        foreach (var descriptor in _services)
        {
            services.Add(descriptor);
        }
    }

    private static object CreateProvider(IServiceProvider services, AutomationProviderId id, Type implementation)
    {
        var provider = (IAutomationProvider)ActivatorUtilities.CreateInstance(services, implementation);
        if (!Equals(provider.Descriptor?.Id, id))
        {
            switch (provider)
            {
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
                case IAsyncDisposable asyncDisposable:
                    // Factories are synchronous; observe the disposal so a failure is not lost as an unobserved exception.
                    _ = asyncDisposable.DisposeAsync().AsTask().ContinueWith(t => t.Exception, TaskScheduler.Default);
                    break;
            }

            throw new InvalidOperationException($"Provider registered as '{id}' reports id '{provider.Descriptor?.Id}'.");
        }

        return provider;
    }

    private void AddServiceDescriptor(ServiceDescriptor descriptor) => _services.Add(descriptor);

    private void RequireOwned(Type type, string what)
    {
        // A plugin may only add its own types; registering host or framework types would let it replace host services.
        if (AssemblyLoadContext.GetLoadContext(type.Assembly) != context)
        {
            _problems.Add($"{what} '{type.FullName}' is not defined by the plugin; plugins can only register their own types.");
        }
    }

    private void RequireConcrete(Type type, string what)
    {
        if (type.IsAbstract || type.IsInterface)
        {
            _problems.Add($"{what} '{type.FullName}' must be a concrete class.");
        }
    }
}
