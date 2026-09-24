namespace MyRPA.Sdk;

/// <summary>Facts about the Automation SDK this assembly implements.</summary>
public static class AutomationSdk
{
    /// <summary>
    /// The SDK contract version implemented by this build (ADR-0013). Plugin manifests declare the version they were
    /// built for; the host accepts them when <see cref="SdkVersion.Supports"/> returns <see langword="true"/>.
    /// </summary>
    public static SdkVersion Version { get; } = new(1, 0);

    /// <summary>
    /// Activity and provider namespace reserved for the built-in library; plugins cannot register names in it.
    /// </summary>
    public const string ReservedNamespace = "Core";
}
