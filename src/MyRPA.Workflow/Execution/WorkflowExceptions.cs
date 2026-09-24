using MyRPA.Core.Activities;
using MyRPA.Core.Identifiers;
using MyRPA.Workflow.Expressions;

namespace MyRPA.Workflow.Execution;

/// <summary>Base class for errors raised deliberately by workflow logic, carrying a stable error code.</summary>
public abstract class WorkflowErrorException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">Stable error code.</param>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Optional cause.</param>
    protected WorkflowErrorException(string code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    /// <summary>Stable error code (see <see cref="ExecutionErrorCodes"/>).</summary>
    public string Code { get; }
}

/// <summary>
/// The activity failure contract (ADR-0013): an activity reports a <em>classified</em> failure by throwing this
/// exception. The node fails with code <see cref="ExecutionErrorCodes.ActivityFailed"/> and the given
/// <see cref="ErrorType"/>, which workflows can inspect in <c>Core.TryCatch</c> (<c>err.errorType</c>).
/// Any other exception also fails the node, classified by its CLR type name.
/// </summary>
public class ActivityFailedException : WorkflowErrorException
{
    /// <summary>Maximum length of an error type.</summary>
    public const int MaxErrorTypeLength = 64;

    /// <summary>Creates the exception.</summary>
    /// <param name="errorType">
    /// Stable, technology-neutral classification such as <c>ElementNotFound</c>: one or more dot-separated segments of
    /// ASCII letters/digits, each starting with a letter.
    /// </param>
    /// <param name="message">Message for the workflow author.</param>
    /// <param name="innerException">Optional technical cause (kept for logs; not shown to the workflow).</param>
    public ActivityFailedException(string errorType, string message, Exception? innerException = null)
        : base(ExecutionErrorCodes.ActivityFailed, message, innerException)
    {
        if (!IsValidErrorType(errorType))
        {
            throw new ArgumentException(
                $"'{errorType}' is not a valid error type (dot-separated ASCII letters/digits, at most {MaxErrorTypeLength} characters).",
                nameof(errorType));
        }

        ErrorType = errorType;
    }

    /// <summary>The failure classification.</summary>
    public string ErrorType { get; }

    /// <summary>Returns <see langword="true"/> when <paramref name="errorType"/> is a valid error type.</summary>
    /// <param name="errorType">Candidate.</param>
    public static bool IsValidErrorType(string? errorType)
    {
        if (string.IsNullOrEmpty(errorType) || errorType.Length > MaxErrorTypeLength)
        {
            return false;
        }

        foreach (var segment in errorType.Split('.'))
        {
            if (segment.Length == 0 || !char.IsAsciiLetter(segment[0]) || !segment.All(char.IsAsciiLetterOrDigit))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Raised by <c>Core.Throw</c>.</summary>
public sealed class WorkflowThrowException : WorkflowErrorException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">User-supplied message.</param>
    public WorkflowThrowException(string message)
        : base(ExecutionErrorCodes.WorkflowThrow, message)
    {
    }
}

/// <summary>Raised when an invoked workflow cannot be resolved, exceeds limits, or does not succeed.</summary>
public sealed class WorkflowInvocationException : WorkflowErrorException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">One of the invocation error codes.</param>
    /// <param name="message">Message.</param>
    /// <param name="childResult">The nested execution result, when it ran.</param>
    public WorkflowInvocationException(string code, string message, WorkflowExecutionResult? childResult = null)
        : base(code, message) => ChildResult = childResult;

    /// <summary>The nested execution result, when it ran.</summary>
    public WorkflowExecutionResult? ChildResult { get; }
}

/// <summary>
/// A node failed. Thrown by the engine at the boundary of the node where the failure originated (exactly once per
/// failure), so callers and <c>Core.TryCatch</c> know which node failed. The original exception is
/// <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class WorkflowActivityException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="nodeId">The failing node.</param>
    /// <param name="activityType">The failing node's activity type.</param>
    /// <param name="innerException">The original failure.</param>
    public WorkflowActivityException(NodeId nodeId, ActivityTypeName activityType, Exception innerException)
        : base(BuildMessage(nodeId, activityType, innerException), innerException ?? throw new ArgumentNullException(nameof(innerException)))
    {
        NodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
        ActivityType = activityType ?? throw new ArgumentNullException(nameof(activityType));
        Code = innerException switch
        {
            WorkflowErrorException error => error.Code,
            WorkflowExpressionException => ExecutionErrorCodes.ExpressionFailed,
            _ => ExecutionErrorCodes.ActivityFailed,
        };
        ErrorType = innerException switch
        {
            ActivityFailedException failed => failed.ErrorType,
            WorkflowThrowException => "Throw",
            WorkflowInvocationException => "InvokeWorkflow",
            WorkflowExpressionException => "Expression",
            _ => innerException.GetType().Name,
        };
    }

    /// <summary>The failing node.</summary>
    public NodeId NodeId { get; }

    /// <summary>The failing node's activity type.</summary>
    public ActivityTypeName ActivityType { get; }

    /// <summary>Stable error code.</summary>
    public string Code { get; }

    /// <summary>
    /// Error kind: the <see cref="ActivityFailedException.ErrorType"/> of a classified failure, <c>Throw</c>,
    /// <c>InvokeWorkflow</c>, <c>Expression</c>, or the exception type name.
    /// </summary>
    public string ErrorType { get; }

    /// <summary>The message of the original failure.</summary>
    public string ErrorMessage => InnerException!.Message;

    /// <summary>Converts to result error details.</summary>
    public ExecutionError ToExecutionError() => new(Code, ErrorMessage, NodeId.Value, ActivityType.Value, ErrorType);

    private static string BuildMessage(NodeId nodeId, ActivityTypeName activityType, Exception? inner) =>
        $"Node '{nodeId}' ({activityType}) failed: {inner?.Message}";
}
