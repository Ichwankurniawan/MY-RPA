using System.Diagnostics;
using System.Reflection;
using MyRPA.Core.Diagnostics;

namespace MyRPA.Runtime.Diagnostics;

/// <summary>
/// Owns the runtime <see cref="System.Diagnostics.ActivitySource"/>. Registered as a DI singleton and disposed with
/// the container — intentionally not a static (ADR-0005, ADR-0006).
/// </summary>
public sealed class MyRpaTelemetry : IDisposable
{
    /// <summary>Creates the telemetry owner.</summary>
    public MyRpaTelemetry()
    {
        var version = typeof(MyRpaTelemetry).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        ActivitySource = new ActivitySource(DiagnosticNames.RuntimeActivitySource, version);
    }

    /// <summary>The runtime activity source, named <see cref="DiagnosticNames.RuntimeActivitySource"/>.</summary>
    public ActivitySource ActivitySource { get; }

    /// <inheritdoc />
    public void Dispose() => ActivitySource.Dispose();
}
