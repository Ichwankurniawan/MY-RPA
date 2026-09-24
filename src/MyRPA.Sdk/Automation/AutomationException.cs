using MyRPA.Workflow.Execution;

namespace MyRPA.Sdk.Automation;

/// <summary>
/// Technology-neutral failure classifications for automation (PRD 4.5). They become the node's <c>errorType</c>, which
/// workflows can test in <c>Core.TryCatch</c>, so they must stay stable.
/// </summary>
public static class AutomationErrorTypes
{
    /// <summary>No element matched the selector.</summary>
    public const string ElementNotFound = "ElementNotFound";

    /// <summary>More than one element matched where one was required.</summary>
    public const string AmbiguousMatch = "AmbiguousMatch";

    /// <summary>A previously found element no longer exists.</summary>
    public const string ElementStale = "ElementStale";

    /// <summary>The selector is malformed or uses a strategy the provider does not support.</summary>
    public const string InvalidSelector = "InvalidSelector";

    /// <summary>The element or provider does not support the requested operation.</summary>
    public const string NotSupported = "NotSupported";

    /// <summary>The provider or the application it automates is not available.</summary>
    public const string ProviderUnavailable = "ProviderUnavailable";

    /// <summary>
    /// An operation-level wait (for example waiting for an element) exceeded its own limit. This is a node
    /// <em>failure</em>, catchable by <c>Core.TryCatch</c>; it is different from the workflow's timeout, which ends the
    /// run with status <c>TimedOut</c>.
    /// </summary>
    public const string OperationTimeout = "OperationTimeout";
}

/// <summary>
/// A classified automation failure (ADR-0013). Fails the node with code <c>MYRPA2001</c> and the given
/// <see cref="ActivityFailedException.ErrorType"/>.
/// </summary>
public class AutomationException : ActivityFailedException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="errorType">Classification, usually one of <see cref="AutomationErrorTypes"/>.</param>
    /// <param name="message">Message for the workflow author.</param>
    /// <param name="innerException">Optional technical cause.</param>
    public AutomationException(string errorType, string message, Exception? innerException = null)
        : base(errorType, message, innerException)
    {
    }
}
