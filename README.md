# ReadUs

A from-scratch, low-allocation .NET Redis client for .NET 10 and Redis
8.10.1+, built as a full replacement for StackExchange.Redis with
first-class blocking-command support (StackExchange.Redis's best-known
gap), Cluster, Sentinel, and managed/remote Redis (AWS ElastiCache/
MemoryDB, Azure Cache, GCP Memorystore, Redis Cloud) support.

RESP3 is negotiated by default. The command surface (~460 commands) is
generated at compile time from Redis's own vendored command table — no
reflection anywhere on the command path, including the optional convenience
packages.

## Features

- **RESP3-first protocol layer** — out-of-band push messages (client-side
  caching, Pub/Sub) handled as first-class citizens, not bolted on.
- **Two-tier connection pooling** — a multiplexed pool for ordinary
  commands and a leased pool for blocking commands and transactions, so a
  slow `BLPOP` never starves everything else sharing a connection.
- **Cluster** — hash-slot routing, transparent `MOVED`/`ASK`/`TRYAGAIN`/
  `CLUSTERDOWN` handling, per-node health tracking with quarantine and
  automatic reintegration, replica read routing, cluster-routed
  transactions and blocking commands, sharded Pub/Sub.
- **Sentinel** — quorum-of-responders master discovery, dual pub/sub +
  polling failover detection.
- **Managed/remote Redis** — TLS (SNI, custom certificate validation,
  mutual TLS), a pluggable rotating-credentials seam (AWS IAM/Azure Entra
  ID-shaped, no cloud SDK dependency), TCP keepalive tuned for cross-AZ
  blips.
- **Transactions and blocking commands** — a `WATCH`/`MULTI`/`EXEC`
  builder and a `CLIENT UNBLOCK`-based cancellation protocol that leaves a
  connection safely reusable (or safely discarded) after a cancelled
  blocking call, never in an ambiguous state.
- **Client-side caching** — RESP3 `CLIENT TRACKING`, both default (per-key)
  and BCAST (prefix) modes, with the read/invalidation race closed by
  striped per-key locking.
- **Pub/Sub** — an `IAsyncEnumerable<RedisPubSubMessage>`-based subscribe
  API with deterministic unsubscribe on scope exit, including cluster
  sharded Pub/Sub.
- **Scripting** — automatic `EVALSHA`→`NOSCRIPT`→`EVAL` fallback with a
  self-computed script hash.
- **Zero-reflection convenience layer** — a source generator maps POCOs
  directly to/from Redis hashes (`ReadUs.Extensions.Hashes`); JSON
  whole-value helpers use `System.Text.Json`'s own source-generated
  `JsonTypeInfo<T>` (`ReadUs.Extensions.Json`) — no runtime reflection
  anywhere, including these optional packages.
- **`IRedisClient`** — one interface implemented by the standalone,
  Cluster, and Sentinel clients alike for the command-execution surface
  they genuinely share.
- **DI and OpenTelemetry** — separate, optional packages; referencing them
  (and only them) is what costs anything.

## Quick start

```csharp
using System.Net;
using ReadUs;
using ReadUs.Connections;
using ReadUs.Generated; // the source-generated typed command surface

await using var client = await RedisClient.ConnectAsync(
    new RedisConnectionOptions { EndPoint = new DnsEndPoint("localhost", 6379) });

await client.SetAsync("greeting"u8.ToArray(), "hello, redis"u8.ToArray());
var value = await client.GetAsync("greeting"u8.ToArray());

Console.WriteLine(value.AsString()); // "hello, redis"
```

Anything the generated typed surface doesn't cover yet (or a module
command like `FT.*`/`JSON.*`) is reachable through the same low-level
escape hatch every generated method itself calls:

```csharp
var reply = await client.ExecuteAsync("PING"u8.ToArray(), []);
```

See [`docs/guides/getting-started.md`](docs/guides/getting-started.md) for
the full walkthrough.

## Install

Not yet published to NuGet. Until then, reference the projects directly —
clone the repo and add a `ProjectReference` to whichever package(s) you
need (`ReadUs.Core` at minimum):

| Package | What it's for |
|---|---|
| `ReadUs.Core` | Standalone client, RESP protocol, pooling, transactions, blocking commands, Pub/Sub, Scripting, client-side caching. |
| `ReadUs.Cluster` | Redis Cluster support. |
| `ReadUs.Sentinel` | Redis Sentinel support. |
| `ReadUs.Extensions.DependencyInjection` | `Microsoft.Extensions.DependencyInjection` registration helpers. |
| `ReadUs.Extensions.OpenTelemetry` | Wires ReadUs's metrics into an app's `MeterProviderBuilder`. |
| `ReadUs.Extensions.Hashes` | Zero-reflection POCO ↔ Redis Hash mapping. |
| `ReadUs.Extensions.Json` | Whole-value JSON POCO helpers over `SET`/`GET`. |

## Documentation

| Guide | Covers |
|---|---|
| [Getting started](docs/guides/getting-started.md) | Install, connect, the generated command surface vs. the raw escape hatch. |
| [Architecture](docs/guides/architecture.md) | Tier 1/Tier 2 pooling, RESP3, the command-table source generator, the zero-allocation goal. |
| [Cluster](docs/guides/cluster.md) | Slot routing, redirects, read preference, quarantine, sharded Pub/Sub. |
| [Sentinel](docs/guides/sentinel.md) | Master discovery, failover detection, the subscriber failover caveat. |
| [Managed Redis and TLS](docs/guides/managed-redis-and-tls.md) | TLS, rotating credentials, DNS re-resolution. |
| [Transactions and blocking commands](docs/guides/transactions-and-blocking-commands.md) | `WATCH`/`MULTI`/`EXEC`, the Tier 2 leased pool, cancellation semantics. |
| [Client-side caching](docs/guides/client-side-caching.md) | `CLIENT TRACKING`, Default vs. BCAST modes. |
| [Pub/Sub](docs/guides/pubsub.md) | Subscribing, unsubscribing, cluster sharded Pub/Sub. |
| [Scripting](docs/guides/scripting.md) | `EVAL`/`EVALSHA` with automatic fallback. |
| [Hash and JSON mapping](docs/guides/hash-and-json-mapping.md) | The two zero-reflection convenience packages. |
| [Dependency injection and observability](docs/guides/dependency-injection-and-observability.md) | DI registration, OpenTelemetry, the full metrics list. |
| [`IRedisClient`](docs/guides/iredisclient.md) | What the shared interface covers, and why some things are deliberately excluded. |

The original design spec and an implementation-level design log (state
machines, invariants, and every non-obvious decision recorded as it was
made) live in [`docs/`](docs/) for anyone digging deeper than the guides
above.

## Performance

See [`docs/benchmarks.md`](docs/benchmarks.md) for full methodology and
results, including a head-to-head comparison against StackExchange.Redis
for equivalent scenarios. Numbers are from a single machine at a single
point in time — a snapshot, not a universal claim; reproduce them yourself
with `dotnet run -c Release --project benchmarks/ReadUs.Benchmarks`
before relying on them for a decision that matters.

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md) — building, testing (Docker
required for integration tests, which run against real, disposable
Testcontainers-managed Redis/Cluster/Sentinel, never mocks), and coding
conventions.

## License

[MIT](LICENSE)
