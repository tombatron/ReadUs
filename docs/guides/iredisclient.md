# `IRedisClient`

`RedisClient`, `ClusterClient`, and `SentinelClient` all implement
`IRedisClient` — the command-execution surface genuinely identical across
all three, so application code that only needs to run commands never has
to branch on which topology it's actually talking to:

```csharp
using ReadUs;

async Task<string?> GetGreetingAsync(IRedisClient client)
{
    var reply = await client.ExecuteAsync("GET"u8.ToArray(), ["greeting"u8.ToArray()]);
    return reply.AsString();
}

// Works identically against any of the three:
await GetGreetingAsync(redisClient);
await GetGreetingAsync(clusterClient);
await GetGreetingAsync(sentinelClient);
```

```csharp
public interface IRedisClient : IAsyncDisposable
{
    ValueTask<RedisResult> ExecuteAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default);
    ValueTask<RedisResult> ExecuteBlockingAsync(ReadOnlyMemory<byte> commandName, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default);
    Task<RedisResult[]> ExecuteBatchAsync(IReadOnlyList<RedisBatchCommand> commands, CancellationToken cancellationToken = default);
}
```

## What's deliberately not on it

Two real members stay off the interface, on purpose, not by oversight —
each is Cluster-specific in a way that genuinely doesn't unify:

- **`ClusterClient`'s `ReadPreference`-overloaded `ExecuteAsync`** — a
  Cluster-only concept (see [Cluster](cluster.md#read-preference));
  `RedisClient`/`SentinelClient` have exactly one node, so there's nothing
  for a read preference to choose between.
- **`BeginTransactionAsync`** — `RedisClient`/`SentinelClient`'s version
  needs no routing key, since there's only ever one node.
  `ClusterClient`'s version needs one, because a Cluster transaction must
  pick a node *before* `WATCH` even runs (the transaction leases its
  connection immediately — see
  [Transactions and blocking commands](transactions-and-blocking-commands.md)).
  Forcing a meaningless key parameter onto the single-node types, or
  having `ClusterClient`'s interface-mandated no-key overload throw
  `NotSupportedException`, would both be worse than just keeping it a
  concrete, type-specific method on `ClusterClient` alone.

This line is drawn deliberately, not just as a workaround — see the
original project spec's own §10: "Cluster/Sentinel-*specific* concerns...
live behind clearly separate, opt-in surface." An interface member that
throws for one implementer by design is exactly the "looks uniform,
isn't" trap that guidance exists to avoid.

## Why `ExecuteBlockingAsync` *does* unify cleanly

Unlike a transaction, a single blocking command's routing key is already
embedded in its own arguments (`BLPOP key timeout`) — `ClusterClient` can
extract it and route by slot the same way it does for
`ExecuteAsync`, with no extra parameter needed. A blocking command sent to
the wrong node still gets an immediate `MOVED` before it ever blocks
(routing happens before the block starts), so it reuses the exact same
redirect-handling loop as an ordinary command internally.

## See also

- [Cluster](cluster.md) / [Sentinel](sentinel.md) — the
  topology-specific surface each client adds beyond this interface.
- [Scripting](scripting.md) — `RedisScript.EvaluateAsync` is the other
  place `IRedisClient` shows up directly, as the one adapter shape that
  lets a script run against any of the three client types.
