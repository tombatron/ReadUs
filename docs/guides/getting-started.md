# Getting started

## Install

Not yet published to NuGet — reference the project directly for now:

```sh
dotnet add reference path/to/ReadUs/src/ReadUs.Core/ReadUs.Core.csproj
```

## Connecting

`RedisClient` is the standalone entry point. It owns two internal
connection pools (see [Architecture](architecture.md)) and should be
created once and kept alive for your application's lifetime — not
reconnected per call or per request.

```csharp
using System.Net;
using ReadUs;
using ReadUs.Connections;

await using var client = await RedisClient.ConnectAsync(
    new RedisConnectionOptions { EndPoint = new DnsEndPoint("localhost", 6379) },
    connectionCount: 4,       // Tier 1 (multiplexed) pool size
    leasedConnectionCount: 4); // Tier 2 (leased) pool size, for blocking commands/transactions
```

`RedisClient` is `IAsyncDisposable` — `await using` (or an explicit
`DisposeAsync()` at shutdown) closes every underlying socket cleanly.

For Cluster or Sentinel deployments, see the
[Cluster](cluster.md)/[Sentinel](sentinel.md) guides — both `ClusterClient`
and `SentinelClient` implement the same [`IRedisClient`](iredisclient.md)
surface as `RedisClient` for ordinary commands.

## The generated typed command surface

ReadUs's command-table source generator reads Redis's own vendored command
metadata at ReadUs's build time and emits a typed extension method for
~90% of Redis's ~460 commands (the remaining ~10% have argument shapes too
deeply nested for the generator to model yet — see
[Architecture](architecture.md) for exactly what that limit is). These live
in the `ReadUs.Generated` namespace as extension methods on `RedisClient`:

```csharp
using ReadUs.Generated;

await client.SetAsync("key"u8.ToArray(), "value"u8.ToArray());
var result = await client.GetAsync("key"u8.ToArray());
Console.WriteLine(result.AsString());

// Optional/flag arguments become named parameters, not a loose object array:
await client.SetAsync(
    "key"u8.ToArray(), "value"u8.ToArray(),
    conditionNx: true,
    expirationSeconds: 60);
```

Every argument is `ReadOnlyMemory<byte>`, not `string` — this client
never assumes an encoding on your behalf. `u8` string literals
(`"key"u8`) are the cheapest way to get UTF-8 bytes for a literal; for a
runtime string, use `Encoding.UTF8.GetBytes(...)`.

## `RedisResult`

Every command returns a `RedisResult` — a single struct type covering
every RESP3 reply shape (simple string, error, integer, bulk string,
array, null, boolean, double, big number, map, set, push). Read it with:

- `.AsString()` / `.AsSpan()` — bulk/simple string, UTF-8 decoded or raw.
- `.AsInt64()` / `.AsDouble()` / `.AsBoolean()`
- `.AsItems()` — array/map/set/push, as a flat `ReadOnlySpan<RedisResult>`
  (a map's items alternate key, value, key, value, ...).
- `.IsNull` / `.IsError`

Reading a value as the wrong shape throws `InvalidOperationException` with
the actual RESP type in the message, rather than returning a default or
silently coercing.

## The raw escape hatch

For anything the generated surface doesn't cover yet, or a module command
(`FT.*`, `JSON.*`, ...), `ExecuteAsync` is the same low-level primitive
every generated method itself calls:

```csharp
var reply = await client.ExecuteAsync(
    "PING"u8.ToArray(),
    args: []);
```

## See also

- [Architecture](architecture.md) — how the pools, protocol layer, and
  source generator actually work.
- [`IRedisClient`](iredisclient.md) — the interface `RedisClient`/
  `ClusterClient`/`SentinelClient` share.
