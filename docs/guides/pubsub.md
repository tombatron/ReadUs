# Pub/Sub

`RedisSubscriber` owns one dedicated connection (subscription state, like
client-side-caching's tracking state, belongs to a single connection) and
exposes subscriptions as `IAsyncEnumerable<RedisPubSubMessage>`:

```csharp
using ReadUs.Connections;
using ReadUs.PubSub;

await using var subscriber = await RedisSubscriber.ConnectAsync(
    new RedisConnectionOptions { EndPoint = new DnsEndPoint("localhost", 6379) });

await foreach (var message in subscriber.SubscribeAsync("channel"))
{
    Console.WriteLine($"{message.Channel}: {message.Payload.AsString()}");
}
```

Publishing is an ordinary command — no dedicated connection needed for it:

```csharp
using ReadUs.Generated;

await client.PublishAsync("channel"u8.ToArray(), "hello"u8.ToArray());
```

`PSubscribeAsync(pattern)` and `SSubscribeAsync(shardChannel)` (shard
pub/sub — see [Cluster](cluster.md#sharded-pubsub) for cluster-aware
routing) follow the same shape. A `RedisPubSubMessage` carries `Kind`
(`Message`/`PMessage`/`SMessage`), `Channel`, `Pattern` (set only for
`PMessage` — the pattern that matched, distinct from the concrete channel
the publish happened on), and `Payload` (an ordinary `RedisResult`, same
as any other reply).

## Unsubscribing is automatic

Breaking out of the `await foreach` loop, cancelling the token passed to
`SubscribeAsync`, or disposing the enumerator early all send the matching
`UNSUBSCRIBE` on the way out — there's no separate `UnsubscribeAsync` call
to remember. This falls out of `SubscribeAsync` being an async iterator
with the unsubscribe wrapped in a `try`/`finally` around the `yield
return` loop.

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

await foreach (var message in subscriber.SubscribeAsync("channel", cts.Token))
{
    if (ShouldStop(message))
    {
        break; // sends UNSUBSCRIBE before this loop actually exits
    }
}
```

## Why this needed a dedicated type, not just a generated method

ReadUs always negotiates RESP3 (see
[Architecture](architecture.md#resp3-by-default)). Under RESP3, the
server sends a `SUBSCRIBE`/`PSUBSCRIBE`/`SSUBSCRIBE` confirmation as an
out-of-band **push** frame, not the ordinary correlated reply every other
command gets. `RedisSubscriber` sends subscription-state commands through
a low-level primitive that writes to the wire without expecting a
correlated reply, and correlates the confirmation (and every subsequent
message) through the same push-handling path client-side caching uses for
invalidations.

## What's not implemented

No auto-reconnect if the dedicated connection faults — an open
subscription's `await foreach` ends with an exception instead (so a
consumer finds out clearly rather than hanging forever), and a caller
needing resilience opens a fresh `RedisSubscriber` and re-subscribes.
Subscribing to several channels in one `SUBSCRIBE` call (Redis's own
multi-argument form) also isn't exposed — call `SubscribeAsync` multiple
times instead, each independently confirmed.

## See also

- [Client-side caching](client-side-caching.md) — the other RESP3-push
  feature, built the same way.
- [Cluster](cluster.md#sharded-pubsub) — shard-aware subscribing.
