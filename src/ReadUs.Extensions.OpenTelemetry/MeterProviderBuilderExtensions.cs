using OpenTelemetry.Metrics;
using ReadUs.Diagnostics;

namespace ReadUs.Extensions.OpenTelemetry;

/// <summary>
/// Wires ReadUs's metrics (project spec §10) into an application's own OpenTelemetry
/// pipeline. Deliberately thin: <c>ReadUs.Core</c> emits everything via the BCL's
/// <see cref="System.Diagnostics.Metrics"/> with no OpenTelemetry dependency at all
/// (see <see cref="ReadUsDiagnostics"/>) — this package's only job is pointing a real
/// <see cref="MeterProviderBuilder"/> at ReadUs's meter name, so referencing it (and
/// only it) is what actually costs anything.
/// </summary>
public static class MeterProviderBuilderExtensions
{
    public static MeterProviderBuilder AddReadUsInstrumentation(this MeterProviderBuilder builder) =>
        builder.AddMeter(ReadUsDiagnostics.MeterName);
}
