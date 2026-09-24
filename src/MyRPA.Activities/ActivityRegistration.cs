using System.Reflection;
using MyRPA.Core.Activities;
using MyRPA.Workflow.Execution;

namespace MyRPA.Activities;

/// <summary>
/// Binds an activity type name to its implementation. Created only by code registration
/// (<see cref="ActivitiesServiceCollectionExtensions.AddActivity{TActivity}"/> or a plugin's registrar), never from
/// workflow input.
/// </summary>
public sealed class ActivityRegistration
{
    /// <summary>Creates a registration.</summary>
    /// <param name="descriptor">Activity metadata.</param>
    /// <param name="implementationType">
    /// A concrete, public <see cref="IActivity"/> type with exactly one public constructor whose parameters are services.
    /// </param>
    /// <param name="source">The id of the plugin that registered the activity, or <see langword="null"/> for the host.</param>
    public ActivityRegistration(ActivityDescriptor descriptor, Type implementationType, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(implementationType);
        if (!typeof(IActivity).IsAssignableFrom(implementationType) || implementationType.IsAbstract || implementationType.IsInterface
            || implementationType.ContainsGenericParameters)
        {
            throw new ArgumentException($"'{implementationType.Name}' must be a concrete {nameof(IActivity)}.", nameof(implementationType));
        }

        var constructors = implementationType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (constructors.Length != 1)
        {
            throw new ArgumentException(
                $"'{implementationType.Name}' must have exactly one public constructor (it has {constructors.Length}).", nameof(implementationType));
        }

        Descriptor = descriptor;
        ImplementationType = implementationType;
        Constructor = constructors[0];
        Source = source;
    }

    /// <summary>Activity metadata.</summary>
    public ActivityDescriptor Descriptor { get; }

    /// <summary>The implementation type, instantiated once per node invocation.</summary>
    public Type ImplementationType { get; }

    /// <summary>The plugin id that registered the activity, or <see langword="null"/> for host-registered activities.</summary>
    public string? Source { get; }

    /// <summary>The public constructor; its parameters are resolved from the run's services.</summary>
    internal ConstructorInfo Constructor { get; }
}
