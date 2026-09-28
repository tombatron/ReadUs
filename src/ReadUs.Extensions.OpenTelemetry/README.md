# ReadUs.Extensions.OpenTelemetry

Wires [ReadUs](https://github.com/tombatron/ReadUs)'s
`System.Diagnostics.Metrics`-based instrumentation into an application's
own OpenTelemetry `MeterProviderBuilder`. `ReadUs.Core` itself takes no
OpenTelemetry dependency — referencing this package (and only it) is what
actually costs anything.

## Install

Not yet published to NuGet — reference the project directly:

```sh
dotnet add reference path/to/ReadUs/src/ReadUs.Extensions.OpenTelemetry/ReadUs.Extensions.OpenTelemetry.csproj
```

## Example

```csharp
using OpenTelemetry.Metrics;
using ReadUs.Extensions.OpenTelemetry;

var builder = Sdk.CreateMeterProviderBuilder()
    .AddReadUsInstrumentation();
```

## Documentation

See the [Dependency injection and observability guide](https://github.com/tombatron/ReadUs/blob/main/docs/guides/dependency-injection-and-observability.md)
(includes the full metrics list) and the
[root README](https://github.com/tombatron/ReadUs).
