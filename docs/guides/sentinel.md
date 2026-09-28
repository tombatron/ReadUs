# Sentinel

`SentinelClient` is purely a discovery/failover layer on top of the
ordinary connection stack — once the current master is known, commands
run through an ordinary `RedisClient` exactly like the standalone client
does. This type's whole job is knowing how to find that master and swap
it out transparently on failover.

```csharp
using System.Net;
using ReadUs.Sentinel;

await using var sentinel = await SentinelClient.ConnectAsync(
    sentinelEndpoints: [new DnsEndPoint("localhost", 26379), new DnsEndPoint("localhost", 26380)],
    serviceName: "mymaster");

await sentinel.ExecuteAsync("SET"u8.ToArray(), ["key"u8.ToArray(), "value"u8.ToArray()]);
```

`SentinelClient` implements the same [`IRedisClient`](iredisclient.md)
surface as `RedisClient`/`ClusterClient` — `ExecuteAsync`,
`ExecuteBlockingAsync`, `ExecuteBatchAsync` all just work, routed to
whichever node is currently the master.

## Master discovery never trusts a single sentinel

Discovery queries every known sentinel concurrently and requires a strict
majority of whoever actually *responded* (not of the full configured set)
to agree on the same address before accepting it — a partitioned client
that can only reach one sentinel will trust that one rather than block
forever, trading strict safety for availability. See the design doc's §5
for the reasoning and the explicit tradeoff this makes.

## Failover detection is two-layered

A dedicated pub/sub connection to a sentinel's `+switch-master` channel is
the fast, primary signal; periodic polling (`SENTINEL
get-master-addr-by-name`) is the safety net in case that connection itself
silently drops. Either path swaps the internal `RedisClient` reference to
point at the new master — in-flight calls on the old connection fail
normally (the connection is disposed once the swap completes), and the
next call after the swap is already routed correctly.

## Pub/Sub doesn't follow failover

```csharp
await using var subscriber = await sentinel.CreateSubscriberAsync();
```

opens a dedicated subscription connection against the *current* master —
but unlike `ExecuteAsync` (which reads the live master reference on every
call), a subscription is one physical connection handed to the caller up
front. A later failover has no way to migrate an already-open subscription
onto the new master's connection. A caller needing subscriptions to
survive failover detects the disconnect (the subscription's `await
foreach` ends with an exception — see [Pub/Sub](pubsub.md)) and calls
`CreateSubscriberAsync` again.

## See also

- [Cluster](cluster.md) — the other topology-aware client, for sharding
  rather than primary/replica failover.
- [`IRedisClient`](iredisclient.md).
