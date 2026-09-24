using Microsoft.Playwright;
using MyRPA.Sdk.Automation;

namespace MyRPA.Browser.Playwright;

/// <summary>
/// The provisional Phase 4 browser selector syntax (ADR-0017), parsed into the SDK <see cref="Selector"/>. Phase 6 owns
/// the final selector format; this syntax covers only what browser activities need now.
/// <list type="bullet">
/// <item><c>css=#login</c> — CSS (also the default when there is no prefix, e.g. <c>#login</c>)</item>
/// <item><c>xpath=//button[1]</c> — XPath (also the default for text starting with <c>/</c> or <c>(</c>)</item>
/// <item><c>text=Sign in</c> — elements containing the text (case-insensitive, whitespace-normalized)</item>
/// <item><c>role=button</c> or <c>role=button|Sign in</c> — ARIA role, optionally with its exact accessible name</item>
/// <item><c>css=form#login &gt;&gt; role=button|Submit</c> — steps separated by <c> &gt;&gt; </c> search within the previous match</item>
/// </list>
/// </summary>
public static class BrowserSelectors
{
    private const string StepSeparator = " >> ";

    /// <summary>Parses selector text.</summary>
    /// <param name="text">Selector text.</param>
    /// <exception cref="AutomationException"><see cref="AutomationErrorTypes.InvalidSelector"/> when the text is invalid.</exception>
    public static Selector Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw Invalid(text, "the selector is empty");
        }

        var steps = new List<SelectorStep>();
        foreach (var part in text.Split(StepSeparator))
        {
            steps.Add(ParseStep(part.Trim(), text));
        }

        return new Selector(PlaywrightBrowserProvider.Id, steps);
    }

    /// <summary>Formats a selector back into selector text (for messages).</summary>
    /// <param name="selector">The selector.</param>
    public static string Format(Selector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return string.Join(StepSeparator, selector.Steps.Select(s => $"{Prefix(s.Strategy)}={s.Value}"));
    }

    /// <summary>Builds a Playwright locator for <paramref name="selector"/> on <paramref name="page"/>.</summary>
    /// <param name="page">The page.</param>
    /// <param name="selector">A selector for this provider.</param>
    internal static ILocator ToLocator(IPage page, Selector selector)
    {
        if (selector.Provider != PlaywrightBrowserProvider.Id)
        {
            throw Invalid(selector.ToString(), $"it is for provider '{selector.Provider}'");
        }

        ILocator? locator = null;
        foreach (var step in selector.Steps)
        {
            locator = step.Strategy switch
            {
                SelectorStrategies.Css => locator is null ? page.Locator("css=" + step.Value) : locator.Locator("css=" + step.Value),
                SelectorStrategies.XPath => locator is null ? page.Locator("xpath=" + step.Value) : locator.Locator("xpath=" + step.Value),
                SelectorStrategies.Text => locator is null ? page.GetByText(step.Value) : locator.GetByText(step.Value),
                SelectorStrategies.Role => Role(page, locator, step.Value),
                _ => throw Invalid(Format(selector), $"strategy '{step.Strategy}' is not supported by the browser provider"),
            };
        }

        return locator!;
    }

    private static ILocator Role(IPage page, ILocator? scope, string value)
    {
        var separator = value.IndexOf('|', StringComparison.Ordinal);
        var roleText = separator < 0 ? value : value[..separator];
        var name = separator < 0 ? null : value[(separator + 1)..];
        var role = ParseRole(roleText) ?? throw Invalid("role=" + value, $"'{roleText}' is not an ARIA role");
        return scope is null
            ? page.GetByRole(role, name is null ? null : new PageGetByRoleOptions { Name = name, Exact = true })
            : scope.GetByRole(role, name is null ? null : new LocatorGetByRoleOptions { Name = name, Exact = true });
    }

    private static AriaRole? ParseRole(string text) =>
        text.Length > 0 && text.All(char.IsAsciiLetter) && Enum.TryParse<AriaRole>(text, ignoreCase: true, out var role) ? role : null;

    private static SelectorStep ParseStep(string part, string whole)
    {
        if (part.Length == 0)
        {
            throw Invalid(whole, "a step is empty");
        }

        var equals = part.IndexOf('=', StringComparison.Ordinal);
        var prefix = equals > 0 ? part[..equals] : null;
        var (strategy, value) = prefix?.ToUpperInvariant() switch
        {
            "CSS" => (SelectorStrategies.Css, part[(equals + 1)..]),
            "XPATH" => (SelectorStrategies.XPath, part[(equals + 1)..]),
            "TEXT" => (SelectorStrategies.Text, part[(equals + 1)..]),
            "ROLE" => (SelectorStrategies.Role, part[(equals + 1)..]),
            _ when part.StartsWith('/') || part.StartsWith('(') => (SelectorStrategies.XPath, part),
            _ => (SelectorStrategies.Css, part),
        };

        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid(whole, $"the '{Prefix(strategy)}' step has no value");
        }

        if (strategy == SelectorStrategies.Role && ParseRole(value.Split('|')[0]) is null)
        {
            throw Invalid(whole, $"'{value.Split('|')[0]}' is not an ARIA role");
        }

        return new SelectorStep(strategy, value);
    }

    private static string Prefix(string strategy) => strategy switch
    {
        SelectorStrategies.XPath => "xpath",
        SelectorStrategies.Text => "text",
        SelectorStrategies.Role => "role",
        _ => "css",
    };

    private static AutomationException Invalid(string? text, string reason) =>
        new(AutomationErrorTypes.InvalidSelector, $"Invalid browser selector '{text}': {reason}.");
}
