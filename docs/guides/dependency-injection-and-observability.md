# Dependency injection and observability

Two separate, optional packages — referencing them (and only them) is
what costs anything; `ReadUs.Core` itself takes no dependency on either
`Microsoft.Extensions.DependencyInjection` or OpenTelemetry.

## `ReadUs.Extensions.DependencyInjection`

```csharp
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ReadUs.Connections;
using ReadUs.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddReadUsClient(new RedisConnectionOptions { EndPoint = new DnsEndPoint("localhost", 6379) });
```

`AddReadUsClusterClient(seedEndpoints, optionsFactory?)` and
`AddReadUsSentinelClient(sentinelEndpoints, serviceName, masterOptionsFactory?)`
register `ClusterClient`/`SentinelClient` the same way. Every client is
registered as a **singleton** — it owns a pool of physical connections
meant to live for the application's lifetime, not be created per request.

### The synchronous-resolve tradeoff

.NET's built-in container has no first-class async factory support, and
connecting a client is inherently async (it opens sockets and completes a
RESP handshake). These helpers block the resolving thread once, the first
time the client is resolved — the same tradeoff most .NET libraries in
this position make (`ConnectionMultiplexer.Connect` does the same). If
that block is unacceptable where it would happen (inside a request path
rather than app startup), resolve the client eagerly during host startup
instead of lazily on first request — e.g. by calling
`app.Services.GetRequiredService<RedisClient>()` once before the app
starts accepting traffic.

## `ReadUs.Extensions.OpenTelemetry`

```csharp
using OpenTelemetry.Metrics;
using ReadUs.Extensions.OpenTelemetry;

var builder = Sdk.CreateMeterProviderBuilder()
    .AddReadUsInstrumentation();
```

ReadUs emits every metric via the BCL's `System.Diagnostics.Metrics`
(meter name `"ReadUs"`) regardless of whether this package is referenced —
`AddReadUsInstrumentation()` is a one-line call that just points a real
`MeterProviderBuilder` at that meter name. With no listener attached (this
package not referenced, or referenced but not configured to listen),
every instrument call is a cheap no-op per the BCL's own design — the
zero-overhead-when-not-referenced property extends to "referenced but not
actually wired up," not just "not referenced at all."

### Metrics

| Instrument | Type | Tags | Meaning |
|---|---|---|---|
| `readus.commands.executed` | Counter | `tier` (`multiplexed`/`leased`) | Commands sent. |
| `readus.command.duration` | Histogram (ms) | `tier` | Round-trip latency. |
| `readus.connections.opened` | Counter | — | Physical connections successfully established. |
| `readus.connections.faulted` | Counter | — | Connections that transitioned to Faulted, ready or not. |
| `readus.connections.active` | UpDownCounter | — | Process-wide count of connections currently Ready. |
| `readus.pool.reconnects` | Counter | — | Tier 1 pool slots successfully healed after a fault. |
| `readus.cluster.redirects` | Counter | `kind` (`moved`/`ask`/`tryagain`/`clusterdown`) | Cluster redirects followed. |

Tags are kept deliberately coarse (tier, redirect kind) rather than
per-command-name — decoding a command name to a string on every call
would be an allocation on the hot path this project otherwise spends real
effort avoiding, a cost that shouldn't exist just because a meter happens
to be listening. `readus.connections.active` is process-wide, not broken
out per client/pool instance — see the design doc's §13 step 7 note for
why (an `ObservableGauge<T>` can't be un-registered per pool instance
without leaking).

## See also

- [Architecture](architecture.md) — the zero-allocation goal these
  metrics are designed not to undermine.
