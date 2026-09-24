using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;

namespace MyRPA.Activities;

/// <summary>
/// Immutable catalog of explicitly registered activities. Serves metadata (<see cref="IActivityCatalog"/>) and creates
/// one instance per node invocation (<see cref="IActivityFactory"/>, ADR-0013). Instances are built by a precompiled
/// factory from the run's services and are not tracked by the DI scope, so the engine alone owns and disposes them.
/// Duplicate type names and unresolvable constructor dependencies are configuration errors.
/// </summary>
public sealed class ActivityCatalog : IActivityCatalog, IActivityFactory
{
    private readonly FrozenDictionary<ActivityTypeName, Entry> _byName;

    /// <summary>Creates the catalog.</summary>
    /// <param name="registrations">Explicit registrations.</param>
    /// <param name="serviceCheck">
    /// When supplied (as in DI), every constructor parameter of every activity must be a registered service, so a missing
    /// dependency fails at startup instead of at the first node that uses the activity.
    /// </param>
    /// <exception cref="InvalidOperationException">Two registrations share a type name, or a dependency is missing.</exception>
    public ActivityCatalog(IEnumerable<ActivityRegistration> registrations, IServiceProviderIsService? serviceCheck = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        var byName = new Dictionary<ActivityTypeName, Entry>();
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration, nameof(registrations));
            if (byName.TryGetValue(registration.Descriptor.TypeName, out var existing))
            {
                throw new InvalidOperationException(
                    $"Activity type '{registration.Descriptor.TypeName}' is registered more than once ({SourceName(existing.Registration)} and {SourceName(registration)}).");
            }

            var missing = serviceCheck is null
                ? []
                : registration.Constructor.GetParameters().Where(p => !serviceCheck.IsService(p.ParameterType)).Select(p => p.ParameterType.Name).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Activity type '{registration.Descriptor.TypeName}' ({SourceName(registration)}) depends on unregistered services: {string.Join(", ", missing)}.");
            }

            byName.Add(registration.Descriptor.TypeName, new Entry(registration, ActivatorUtilities.CreateFactory(registration.ImplementationType, Type.EmptyTypes)));
        }

        _byName = byName.ToFrozenDictionary();
        Registrations = [.. byName.Values.Select(e => e.Registration).OrderBy(r => r.Descriptor.TypeName.Value, StringComparer.Ordinal)];
        Descriptors = [.. Registrations.Select(r => r.Descriptor)];
    }

    /// <inheritdoc />
    public IReadOnlyList<ActivityDescriptor> Descriptors { get; }

    /// <summary>All registrations, ordered by type name (includes the registering plugin, if any).</summary>
    public IReadOnlyList<ActivityRegistration> Registrations { get; }

    /// <inheritdoc />
    public bool TryGet(ActivityTypeName typeName, [NotNullWhen(true)] out ActivityDescriptor? descriptor)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        descriptor = _byName.TryGetValue(typeName, out var entry) ? entry.Registration.Descriptor : null;
        return descriptor is not null;
    }

    /// <inheritdoc />
    public IActivity Create(ActivityTypeName typeName, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        ArgumentNullException.ThrowIfNull(services);
        if (!_byName.TryGetValue(typeName, out var entry))
        {
            throw new InvalidOperationException($"Activity type '{typeName}' is not registered.");
        }

        return (IActivity)entry.Factory(services, null);
    }

    private static string SourceName(ActivityRegistration registration) =>
        registration.Source is null ? "host" : $"plugin '{registration.Source}'";

    private sealed record Entry(ActivityRegistration Registration, ObjectFactory Factory);
}
