using System.Diagnostics.CodeAnalysis;

namespace MyRPA.Sdk.Automation;

/// <summary>
/// Identity of an automation provider, such as <c>Browser.Playwright</c> or <c>Demo.Text</c>: two or more dot-separated
/// segments of ASCII letters/digits (the same format as activity type names). Compared ordinally.
/// </summary>
public sealed record AutomationProviderId
{
    /// <summary>Maximum length.</summary>
    public const int MaxLength = 128;

    /// <summary>Creates a provider id.</summary>
    /// <param name="value">Id text.</param>
    /// <exception cref="ArgumentException">The id is not valid.</exception>
    public AutomationProviderId(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException($"'{value}' is not a valid provider id. Expected 'Namespace.Name', such as 'Browser.Playwright'.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The id text.</summary>
    public string Value { get; }

    /// <summary>The first segment, which identifies the owning namespace.</summary>
    public string Namespace => Value[..Value.IndexOf('.', StringComparison.Ordinal)];

    /// <summary>Returns <see langword="true"/> when <paramref name="value"/> is a valid provider id.</summary>
    /// <param name="value">Candidate.</param>
    public static bool IsValid([NotNullWhen(true)] string? value) => Plugins.DottedName.IsValid(value, minimumSegments: 2, MaxLength);

    /// <summary>Tries to create a provider id without throwing.</summary>
    /// <param name="value">Candidate.</param>
    /// <param name="id">The id when valid.</param>
    public static bool TryCreate(string? value, [NotNullWhen(true)] out AutomationProviderId? id)
    {
        id = IsValid(value) ? new AutomationProviderId(value) : null;
        return id is not null;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Metadata of an automation provider.</summary>
public sealed class AutomationProviderDescriptor
{
    /// <summary>Creates the descriptor.</summary>
    /// <param name="id">Provider id.</param>
    /// <param name="displayName">Human-readable name.</param>
    /// <param name="technology">
    /// Technology category (PRD 3.3), for example <c>Browser</c>, <c>Windows</c>, <c>Api</c>, <c>Office</c>, <c>Python</c>,
    /// <c>JavaScript</c>, <c>Ai</c>, <c>Mcp</c>, <c>Storage</c> or <c>Demo</c>. Free text; used for grouping only.
    /// </param>
    /// <param name="description">Optional description.</param>
    public AutomationProviderDescriptor(AutomationProviderId id, string displayName, string technology, string? description = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(technology);
        DisplayName = displayName;
        Technology = technology;
        Description = description;
    }

    /// <summary>Provider id.</summary>
    public AutomationProviderId Id { get; }

    /// <summary>Human-readable name.</summary>
    public string DisplayName { get; }

    /// <summary>Technology category.</summary>
    public string Technology { get; }

    /// <summary>Optional description.</summary>
    public string? Description { get; }
}

/// <summary>
/// The boundary between workflow activities and an automation technology (PRD 7.2, ADR-0013):
/// <c>Workflow activity → technology interface (derives from IAutomationProvider) → provider implementation →
/// technology</c>. Each technology defines its own interface (for example <c>IBrowserProvider</c>) deriving from this
/// one; activities depend on that interface through constructor injection and never on the implementation or the
/// underlying library.
/// </summary>
/// <remarks>
/// Providers are registered with plugin lifetime (<see cref="Plugins.IPluginRegistrar.AddProvider{TService,TProvider}"/>):
/// one instance shared by every run and thread, so implementations must be thread-safe and must keep per-run state in
/// run-lifetime services, not in fields. A provider that owns resources implements <see cref="IAsyncDisposable"/> or
/// <see cref="IDisposable"/> and is disposed when the host shuts down.
/// </remarks>
public interface IAutomationProvider
{
    /// <summary>Provider metadata; <see cref="AutomationProviderDescriptor.Id"/> must match the registration.</summary>
    AutomationProviderDescriptor Descriptor { get; }
}
