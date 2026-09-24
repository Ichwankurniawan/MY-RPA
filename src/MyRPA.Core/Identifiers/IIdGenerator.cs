namespace MyRPA.Core.Identifiers;

/// <summary>
/// Creates execution and correlation identifiers. Injected so that hosts control the clock and tests can produce
/// deterministic identifiers (ADR-0010). There is intentionally no static factory that bypasses this abstraction.
/// </summary>
public interface IIdGenerator
{
    /// <summary>Creates a new, unique execution identifier.</summary>
    ExecutionId NewExecutionId();

    /// <summary>Creates a new, unique correlation identifier.</summary>
    CorrelationId NewCorrelationId();
}
