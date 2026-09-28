# Client-side caching

`ClientSideCache` is a local, read-through cache backed by RESP3 `CLIENT
TRACKING` — the server pushes an invalidation the moment a tracked key
changes, delivered as an ordinary out-of-band push frame on the same
connection (see [Architecture](architecture.md#resp3-by-default)), so
there's no polling and no second connection needed.

It owns one dedicated connection — tracking is a property of a single
connection, so it can't be layered transparently over the ordinary Tier 1
pool the way most commands are.

```csharp
using ReadUs.Caching;
using ReadUs.Connections;

await using var cache = await ClientSideCache.ConnectAsync(
    new RedisConnectionOptions { EndPoint = new DnsEndPoint("localhost", 6379) });

var value = await cache.GetAsync("key"); // reads through to the server, populates the cache
var again = await cache.GetAsync("key"); // served from the local cache

cache.TryGetCached("key", out var cached); // synchronous, no I/O
```

## Two tracking modes

```csharp
using ReadUs.Caching;

// Default: the server remembers exactly the keys this connection has read.
await using var cache = await ClientSideCache.ConnectAsync(options, TrackingMode.Default);

// BCAST: the server invalidates every write under the given prefixes,
// regardless of whether this connection ever read the key.
await using var bcastCache = await ClientSideCache.ConnectAsync(
    options, TrackingMode.Bcast("session:", "user:"));
```

- **Default** — best for a small, hot working set. The server's
  per-connection tracking table grows with how many distinct keys get
  read, and can overflow (falling back to a blanket "flush everything"
  invalidation) if that set gets too large.
- **BCAST** — no per-key server-side table, so it scales to an
  arbitrarily large key space, at the cost of some invalidations for keys
  this particular cache instance never actually touched. `Bcast()` with no
  prefixes covers every key in the keyspace. In BCAST mode with explicit
  prefixes, `GetAsync` rejects a key outside every registered prefix
  client-side, with `ArgumentException`, before touching the network —
  the server would never send this connection an invalidation for that
  key, so caching it anyway would create a value this cache can never
  learn is stale.

## The race this closes

An invalidation for a key can arrive from the server at any point between
issuing that key's `GET` and this cache actually populating itself with
the result — including in the narrow window while the read is still in
flight. `ClientSideCache` closes this with a per-key striped lock making
the read-through's post-read completion and the invalidation handler's
per-key handling atomic with respect to each other (neither ever holds
the lock across the network round trip itself). See the design doc's
"Recorded during implementation" note under §13 step 7 for the two earlier
attempts that got this subtly wrong and the concurrent stress test that
caught it.

## What's not implemented

Redirected tracking (`CLIENT TRACKING ... REDIRECT`) doesn't exist here,
deliberately — it exists in the Redis protocol for RESP2 clients, which
have no way to receive an unsolicited invalidation on an ordinary command
connection and so need a second, dedicated pub/sub connection to receive
it on instead. ReadUs always negotiates RESP3, which delivers
invalidations as an ordinary push on the same connection — there's no gap
a redirect target would close.

There's also no auto-reconnect if the dedicated connection faults; open a
fresh `ClientSideCache` if that happens (the same scope limit
[`RedisSubscriber`](pubsub.md) makes for the same reason).

## See also

- [Pub/Sub](pubsub.md) — the other RESP3-push-based feature, same
  dedicated-connection shape.
