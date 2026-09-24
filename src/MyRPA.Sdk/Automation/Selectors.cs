using System.Diagnostics.CodeAnalysis;

namespace MyRPA.Sdk.Automation;

/// <summary>
/// Well-known selector strategies (PRD 6.1). A strategy names how a <see cref="SelectorStep"/> value is interpreted;
/// providers declare which strategies they understand and may define their own (for example <c>Demo.Field</c>).
/// </summary>
public static class SelectorStrategies
{
    /// <summary>CSS selector.</summary>
    public const string Css = "Css";

    /// <summary>XPath expression.</summary>
    public const string XPath = "XPath";

    /// <summary>Visible text.</summary>
    public const string Text = "Text";

    /// <summary>Semantic role (for example <c>button</c>), optionally with an accessible name.</summary>
    public const string Role = "Role";

    /// <summary>Accessibility name or label.</summary>
    public const string Accessibility = "Accessibility";

    /// <summary>Attribute match (for example <c>name=q</c>).</summary>
    public const string Attributes = "Attributes";

    /// <summary>UI Automation <c>AutomationId</c>.</summary>
    public const string AutomationId = "AutomationId";

    /// <summary>Returns <see langword="true"/> when <paramref name="strategy"/> is a syntactically valid strategy name.</summary>
    /// <param name="strategy">Candidate.</param>
    public static bool IsValid([NotNullWhen(true)] string? strategy) => Plugins.DottedName.IsValid(strategy, minimumSegments: 1, maxLength: 64);
}

/// <summary>One step of a <see cref="Selector"/>: a strategy and its value.</summary>
public sealed record SelectorStep
{
    /// <summary>Creates a step.</summary>
    /// <param name="strategy">A strategy name (see <see cref="SelectorStrategies"/>).</param>
    /// <param name="value">The strategy-specific value, such as <c>#login</c> for <see cref="SelectorStrategies.Css"/>.</param>
    public SelectorStep(string strategy, string value)
    {
        if (!SelectorStrategies.IsValid(strategy))
        {
            throw new ArgumentException($"'{strategy}' is not a valid selector strategy name.", nameof(strategy));
        }

        ArgumentNullException.ThrowIfNull(value);
        Strategy = strategy;
        Value = value;
    }

    /// <summary>Strategy name.</summary>
    public string Strategy { get; }

    /// <summary>Strategy-specific value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Strategy}={Value}";
}

/// <summary>
/// A technology-neutral description of how to find elements (ADR-0013): the provider that resolves it and an ordered
/// path of steps, each narrowing the search to within the previous step's match (window → pane → button,
/// frame → element). Selectors are data, never code; how they are written in workflow files and how alternatives are
/// ranked is defined with the selector engine in Phase 6.
/// </summary>
public sealed class Selector
{
    /// <summary>Creates a selector.</summary>
    /// <param name="provider">The provider that resolves the selector.</param>
    /// <param name="steps">One or more steps.</param>
    public Selector(AutomationProviderId provider, IReadOnlyList<SelectorStep> steps)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0 || steps.Any(s => s is null))
        {
            throw new ArgumentException("A selector needs at least one step and no null steps.", nameof(steps));
        }

        Steps = [.. steps];
    }

    /// <summary>The provider that resolves the selector.</summary>
    public AutomationProviderId Provider { get; }

    /// <summary>The steps, outermost first.</summary>
    public IReadOnlyList<SelectorStep> Steps { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Provider}: {string.Join(" > ", Steps)}";
}

/// <summary>
/// Resolves selectors to elements (PRD 6.2: selector → provider → candidate elements → match → element). Implemented by
/// providers that can find elements; contract only in Phase 3.
/// </summary>
public interface ISelectorResolver
{
    /// <summary>
    /// Finds the elements matching <paramref name="selector"/> now. Returns an empty match when nothing matches; waiting
    /// and retry policy belong to the calling activity, bounded by <paramref name="cancellationToken"/>.
    /// </summary>
    /// <param name="selector">The selector; its provider must be this resolver's provider.</param>
    /// <param name="root">Element to search within, or <see langword="null"/> for the provider's default root.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="AutomationException"><see cref="AutomationErrorTypes.InvalidSelector"/> when the selector is invalid.</exception>
    ValueTask<SelectorMatch> ResolveAsync(Selector selector, IAutomationElement? root, CancellationToken cancellationToken);
}

/// <summary>The elements found for a selector, in document order.</summary>
public sealed class SelectorMatch
{
    /// <summary>Creates a match.</summary>
    /// <param name="selector">The resolved selector.</param>
    /// <param name="elements">The matching elements (possibly none).</param>
    public SelectorMatch(Selector selector, IReadOnlyList<IAutomationElement> elements)
    {
        Selector = selector ?? throw new ArgumentNullException(nameof(selector));
        ArgumentNullException.ThrowIfNull(elements);
        Elements = [.. elements];
    }

    /// <summary>The resolved selector.</summary>
    public Selector Selector { get; }

    /// <summary>The matching elements.</summary>
    public IReadOnlyList<IAutomationElement> Elements { get; }

    /// <summary>Whether at least one element matched.</summary>
    public bool IsFound => Elements.Count > 0;

    /// <summary>
    /// Returns the only matching element, or throws <see cref="AutomationException"/> with
    /// <see cref="AutomationErrorTypes.ElementNotFound"/> or <see cref="AutomationErrorTypes.AmbiguousMatch"/>.
    /// </summary>
    public IAutomationElement RequireSingle() => Elements.Count switch
    {
        1 => Elements[0],
        0 => throw new AutomationException(AutomationErrorTypes.ElementNotFound, $"No element matches '{Selector}'."),
        _ => throw new AutomationException(AutomationErrorTypes.AmbiguousMatch, $"{Elements.Count} elements match '{Selector}'; expected one."),
    };
}
