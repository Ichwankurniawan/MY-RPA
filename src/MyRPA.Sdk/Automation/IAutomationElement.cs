namespace MyRPA.Sdk.Automation;

/// <summary>
/// A technology-neutral handle to one element found by a provider (a DOM node, a UI Automation element, a field of a
/// document), keeping the useful separation "find the element, then act on it" (ADR-0013). Technologies extend it
/// with their own interfaces (for example <c>IBrowserElement</c>) for operations that are not universal.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>An element is owned by the provider or session that returned it and is valid only while that owner is. It is
/// runtime state: it is never stored in workflow variables, which hold only canonical values.</item>
/// <item>Every operation takes a <see cref="CancellationToken"/> and must honor it.</item>
/// <item>An operation the element does not support throws <see cref="AutomationException"/> with
/// <see cref="AutomationErrorTypes.NotSupported"/>; an element that no longer exists throws
/// <see cref="AutomationErrorTypes.ElementStale"/>.</item>
/// </list>
/// </remarks>
public interface IAutomationElement
{
    /// <summary>The provider that produced the element.</summary>
    AutomationProviderId Provider { get; }

    /// <summary>A short human-readable description for logs and error messages (never secrets).</summary>
    string Description { get; }

    /// <summary>Clicks (activates) the element.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask ClickAsync(CancellationToken cancellationToken);

    /// <summary>Types <paramref name="text"/> into the element.</summary>
    /// <param name="text">Text to type.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask TypeTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>Returns the element's text content or value.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<string> GetTextAsync(CancellationToken cancellationToken);

    /// <summary>Returns a named attribute or property, or <see langword="null"/> when the element does not have it.</summary>
    /// <param name="name">Attribute name.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    ValueTask<string?> GetAttributeAsync(string name, CancellationToken cancellationToken);
}
