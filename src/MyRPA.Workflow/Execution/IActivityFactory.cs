using MyRPA.Core.Activities;

namespace MyRPA.Workflow.Execution;

/// <summary>
/// Creates activity instances for registered type names (ADR-0010, ADR-0013). The mapping from name to implementation
/// comes from explicit code registration, never from workflow input.
/// </summary>
public interface IActivityFactory
{
    /// <summary>
    /// Creates a new instance of the activity implementing <paramref name="typeName"/>. The caller owns the instance: it
    /// executes it once and disposes it (<see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>). The instance is
    /// not tracked by <paramref name="services"/>, so nothing retains it after disposal.
    /// </summary>
    /// <param name="typeName">Registered activity type name.</param>
    /// <param name="services">The execution's scoped service provider (supplies constructor dependencies).</param>
    /// <exception cref="InvalidOperationException">The type is not registered.</exception>
    IActivity Create(ActivityTypeName typeName, IServiceProvider services);
}
