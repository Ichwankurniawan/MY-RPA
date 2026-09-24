namespace MyRPA.Workflow.Execution;

/// <summary>
/// Behavior of an activity type — the central Automation SDK contract (ADR-0013, frozen for SDK 1.0).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Registration:</b> explicit, by code (<c>AddActivity</c> or a plugin's registrar); never discovered.</item>
/// <item><b>Lifetime:</b> one instance per node invocation. The engine creates the instance (constructor dependencies
/// come from the run's services), calls <see cref="ExecuteAsync"/> once, and then disposes it
/// (<see cref="IAsyncDisposable"/> preferred over <see cref="IDisposable"/>), whether the invocation succeeded, failed
/// or was cancelled. Resources that must outlive one invocation (browser sessions, native handles, clients) belong to
/// run- or plugin-lifetime services, never to the activity instance.</item>
/// <item><b>Failures:</b> throw. <see cref="ActivityFailedException"/> reports a classified failure; any other exception
/// is a node failure too. The engine attributes the failure to the node exactly once.</item>
/// <item><b>Cancellation:</b> observe <see cref="IActivityContext.CancellationToken"/> and let
/// <see cref="OperationCanceledException"/> propagate; never swallow it.</item>
/// <item><b>Outputs:</b> assign declared assignment-target properties with <see cref="IActivityContext.SetValue"/>.</item>
/// </list>
/// </remarks>
public interface IActivity
{
    /// <summary>Executes the node described by <see cref="IActivityContext.Node"/>.</summary>
    /// <param name="context">Node, property evaluation, variables, child execution and cancellation.</param>
    /// <returns><see cref="ActivityResult.Completed"/> when the node completed.</returns>
    ValueTask<ActivityResult> ExecuteAsync(IActivityContext context);
}

/// <summary>
/// Outcome of a node invocation that did not fail. Frozen for SDK 1.0 (ADR-0013): the only outcome is
/// <see cref="Completed"/>. Failure is an exception, cancellation is <see cref="OperationCanceledException"/>, output
/// values are assigned through <see cref="IActivityContext.SetValue"/>, and diagnostics go to logs and traces — so the
/// result deliberately carries no status, error, values or metadata that would duplicate those channels or
/// <see cref="WorkflowExecutionResult"/>.
/// </summary>
/// <remarks>
/// New outcomes (for example suspension for human approval, PRD 9.5) may be added in a later SDK minor version as new
/// static members; existing activities remain valid because returning <see cref="Completed"/> keeps its meaning.
/// </remarks>
public sealed class ActivityResult
{
    private ActivityResult()
    {
    }

    /// <summary>The activity completed.</summary>
    public static ActivityResult Completed { get; } = new();

    /// <summary>A completed result as a <see cref="ValueTask{TResult}"/>, for synchronous activities.</summary>
    public static ValueTask<ActivityResult> CompletedTask => ValueTask.FromResult(Completed);
}
