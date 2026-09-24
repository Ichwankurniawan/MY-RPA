using MyRPA.Sdk.Automation;
using MyRPA.Workflow.Values;

namespace MyRPA.Samples.DemoPlugin;

/// <summary>
/// The technology interface activities depend on — the pattern a real provider follows
/// (<c>IBrowserProvider</c>, <c>IWindowsAutomationProvider</c>, ...). Here the "technology" is an in-memory document: a
/// workflow Dictionary whose entries are the elements.
/// </summary>
public interface IDemoTextProvider : IAutomationProvider, ISelectorResolver
{
    /// <summary>Opens a document; the returned element is the root for <see cref="ISelectorResolver.ResolveAsync"/>.</summary>
    /// <param name="fields">Field names and canonical values.</param>
    IAutomationElement OpenDocument(IReadOnlyDictionary<string, object?> fields);
}

/// <summary>
/// Sample provider: resolves <see cref="FieldStrategy"/> selectors against an in-memory document. Stateless, so one
/// instance safely serves every run (plugin lifetime).
/// </summary>
public sealed class DemoTextProvider : IDemoTextProvider
{
    /// <summary>The provider id declared in the manifest.</summary>
    public static AutomationProviderId Id { get; } = new("Demo.Text");

    /// <summary>Selector strategy: the value is a field name.</summary>
    public const string FieldStrategy = "Demo.Field";

    /// <inheritdoc />
    public AutomationProviderDescriptor Descriptor { get; } =
        new(Id, "Demo text", "Demo", "Finds fields of an in-memory document (sample provider).");

    /// <inheritdoc />
    public IAutomationElement OpenDocument(IReadOnlyDictionary<string, object?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return new DocumentElement(fields);
    }

    /// <inheritdoc />
    public ValueTask<SelectorMatch> ResolveAsync(Selector selector, IAutomationElement? root, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);
        cancellationToken.ThrowIfCancellationRequested();

        if (selector.Provider != Id)
        {
            throw new AutomationException(AutomationErrorTypes.InvalidSelector, $"'{selector}' is for provider '{selector.Provider}', not '{Id}'.");
        }

        if (root is not DocumentElement document)
        {
            throw new AutomationException(AutomationErrorTypes.InvalidSelector, "Demo.Text selectors resolve within a document opened by this provider.");
        }

        if (selector.Steps is not [{ Strategy: FieldStrategy } step])
        {
            throw new AutomationException(AutomationErrorTypes.InvalidSelector, $"Demo.Text supports exactly one '{FieldStrategy}' step; got '{selector}'.");
        }

        IAutomationElement[] elements = document.Fields.TryGetValue(step.Value, out var value) ? [new FieldElement(step.Value, value)] : [];
        return ValueTask.FromResult(new SelectorMatch(selector, elements));
    }

    private sealed class DocumentElement(IReadOnlyDictionary<string, object?> fields) : IAutomationElement
    {
        public IReadOnlyDictionary<string, object?> Fields { get; } = fields;

        public AutomationProviderId Provider => Id;

        public string Description => $"document ({Fields.Count} fields)";

        public ValueTask ClickAsync(CancellationToken cancellationToken) => throw NotSupported("click");

        public ValueTask TypeTextAsync(string text, CancellationToken cancellationToken) => throw NotSupported("type into");

        public ValueTask<string> GetTextAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(string.Join(", ", Fields.Keys.Order(StringComparer.Ordinal)));

        public ValueTask<string?> GetAttributeAsync(string name, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
    }

    private sealed class FieldElement(string name, object? value) : IAutomationElement
    {
        public AutomationProviderId Provider => Id;

        public string Description => $"field '{name}'";

        public ValueTask ClickAsync(CancellationToken cancellationToken) => throw NotSupported("click");

        public ValueTask TypeTextAsync(string text, CancellationToken cancellationToken) => throw NotSupported("type into");

        public ValueTask<string> GetTextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(WorkflowValues.ToDisplayString(value));
        }

        public ValueTask<string?> GetAttributeAsync(string attribute, CancellationToken cancellationToken) =>
            ValueTask.FromResult(attribute == "name" ? name : null);
    }

    private static AutomationException NotSupported(string operation) =>
        new(AutomationErrorTypes.NotSupported, $"Demo documents are read-only; cannot {operation} an element.");
}
