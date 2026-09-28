# Cluster

`ClusterClient` speaks to a Redis Cluster: it discovers topology via
`CLUSTER SHARDS`, routes every command by the hash slot its key(s) hash
to, and follows `MOVED`/`ASK` redirects and `TRYAGAIN` backoff
transparently. One `RedisClient` (with its own Tier 1/Tier 2 pools) is
kept per discovered node — routing is a layer on top of the ordinary
connection machinery described in [Architecture](architecture.md), not a
second one.

```csharp
using System.Net;
using ReadUs.Cluster;

await using var cluster = await ClusterClient.ConnectAsync(
    seedEndpoints: [new DnsEndPoint("localhost", 7001), new DnsEndPoint("localhost", 7002)]);

await cluster.ExecuteAsync("SET"u8.ToArray(), ["key"u8.ToArray(), "value"u8.ToArray()]);
var reply = await cluster.ExecuteAsync("GET"u8.ToArray(), ["key"u8.ToArray()]);
```

Only seed endpoints are needed — the full topology (and every other
node's address) is discovered from whichever seed answers first.

## CROSSSLOT is checked before anything is sent

A multi-key command whose keys hash to different slots is rejected
client-side, before a byte reaches the network, with
`ClusterCrossSlotException` — the same check the server would make, just
earlier.

## Redirects and node health

`MOVED`, `ASK`, and `TRYAGAIN` are all followed transparently — a caller
never sees them. `CLUSTERDOWN` surfaces as `ClusterDownException` (nothing
to retry into). A node that fails repeatedly (a connection-level failure,
never an ordinary RESP error reply — those mean the node answered, the
opposite of what this tracks) is quarantined: further commands to it fail
fast with `ClusterNodeQuarantinedException` instead of paying for a
doomed connection attempt, while a background prober keeps `PING`ing it
and reintegrates it automatically once it recovers.

## Read preference

By default every command goes to the shard's primary. A read-only command
can opt into replica routing per call:

```csharp
using ReadUs.Cluster.Routing;

var reply = await cluster.ExecuteAsync(
    "GET"u8.ToArray(), ["key"u8.ToArray()],
    ReadPreference.PreferReplica);
```

- `PrimaryOnly` (default) — always the primary.
- `PreferReplica` — a healthy replica if the shard has one, otherwise
  falls back to the primary.
- `ReplicaOnly` — a healthy replica only; throws rather than falling back
  if none is available.
- `RoundRobin` — round-robins across the primary and all its replicas.

A non-primary preference on a write command throws `ArgumentException`
before anything is sent — writes always go to the primary regardless.

A client-wide default is also available, applied automatically to every
read-only command sent through the plain `ExecuteAsync` overload (writes
are never affected, regardless of the default):

```csharp
await using var cluster = await ClusterClient.ConnectAsync(
    seedEndpoints,
    defaultReadPreference: ReadPreference.PreferReplica);
```

## Blocking commands

`ExecuteBlockingAsync` routes and redirects exactly like the plain
`ExecuteAsync` overload, then leases a Tier 2 connection on whichever
node the command ultimately lands on:

```csharp
var popped = await cluster.ExecuteBlockingAsync(
    "BLPOP"u8.ToArray(), ["queue"u8.ToArray(), "5"u8.ToArray()]);
```

## Transactions

A Cluster transaction has to pick a node *before* `WATCH` even runs — the
transaction's connection is leased immediately (see
[Transactions and blocking commands](transactions-and-blocking-commands.md)).
`BeginTransactionAsync` takes the routing key that decides which node:

```csharp
await using (var transaction = await cluster.BeginTransactionAsync("key"u8.ToArray()))
{
    await transaction.WatchAsync(["key"u8.ToArray()]);
    await transaction.MultiAsync();
    await transaction.QueueAsync("SET"u8.ToArray(), ["key"u8.ToArray(), "value"u8.ToArray()]);
    var execResult = await transaction.ExecAsync();
}
```

Every key this transaction later watches or queues a command against must
hash to the same slot as the routing key — ReadUs doesn't validate that
client-side across the whole transaction (it would need to track every
key touched across several independent calls), so a key outside that slot
surfaces as the server's own rejection rather than an early client-side
exception. This is deliberately excluded from
[`IRedisClient`](iredisclient.md) — `RedisClient`/`SentinelClient`'s
single-node `BeginTransactionAsync()` needs no routing key at all.

## Sharded Pub/Sub

`SSUBSCRIBE`/`SPUBLISH` delivery is shard-local in real Redis Cluster —
unlike ordinary `PUBLISH`, which propagates cluster-wide regardless of
which node a subscriber connects to. `CreateShardSubscriberAsync` resolves
the shard channel's owning node before subscribing:

```csharp
await using var subscriber = await cluster.CreateShardSubscriberAsync("channel");

await foreach (var message in subscriber.SSubscribeAsync("channel"))
{
    Console.WriteLine(message.Payload.AsString());
    break;
}
```

See [Pub/Sub](pubsub.md) for the full subscription API.

## See also

- [Sentinel](sentinel.md) — the other topology-aware client, for
  primary/replica failover rather than sharding.
- [`IRedisClient`](iredisclient.md) — what `ClusterClient` shares with
  `RedisClient`/`SentinelClient`, and what stays Cluster-specific.
