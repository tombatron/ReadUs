# Transactions and blocking commands

Both of these run over the **Tier 2 leased pool** described in
[Architecture](architecture.md) — a connection handed to the caller
exclusively for the duration of the call, rather than shared with
everyone else's ordinary traffic on Tier 1. This is deliberate: `WATCH`
and `EXEC` are only meaningful on the same physical connection, and a
blocking command holding a shared connection hostage would stall every
other command multiplexed on it.

## Transactions

```csharp
await using var transaction = await client.BeginTransactionAsync();

await transaction.WatchAsync(["key"u8.ToArray()]);
await transaction.MultiAsync();
await transaction.QueueAsync("SET"u8.ToArray(), ["key"u8.ToArray(), "value"u8.ToArray()]);
var result = await transaction.ExecAsync();

if (result.IsNull)
{
    // A watched key changed before EXEC — the transaction was aborted server-side.
}
```

- `WatchAsync` must be called before `MultiAsync` (`WATCH` after `MULTI`
  is a protocol error on the server).
- `QueueAsync`'s own reply is just `QUEUED` (or an error if the command
  was malformed) — the real per-command results only come back from
  `ExecAsync`, as an array in submission order.
- `ExecAsync`'s result is `IsNull` if any watched key changed —
  Redis's own signal that the transaction was aborted, not executed.
- `DiscardAsync` cancels an open transaction explicitly. If a transaction
  is disposed without calling `ExecAsync`/`DiscardAsync` (an exception
  partway through, say), it's discarded defensively so the connection
  never returns to the pool sitting mid-`MULTI`.

`ClusterClient.BeginTransactionAsync` needs an extra routing-key parameter
a single-node client doesn't — see [Cluster](cluster.md#transactions) for
why, and why that keeps it off the shared
[`IRedisClient`](iredisclient.md) interface.

## Blocking commands

```csharp
var popped = await client.ExecuteBlockingAsync(
    "BLPOP"u8.ToArray(), ["queue"u8.ToArray(), "5"u8.ToArray()]);
```

(No generated typed method targets the leased-connection/blocking path
yet — see the design doc's §5 for why; use the raw command name/args
shape shown above.)

### Cancellation

Cancelling a blocking call mid-wait doesn't just abandon it — ReadUs
issues `CLIENT UNBLOCK <id> TIMEOUT` on a small side channel and waits
(bounded, currently a fixed 2 seconds) for the primary connection to
settle before deciding the outcome:

- If the server confirms it actually unblocked the client, the call
  completes with `OperationCanceledException` and the connection goes
  back to the pool, reusable.
- If a genuine reply had already been produced before the server could
  act on the unblock (a narrow race), that reply is honored rather than
  discarded — the caller already effectively paid for it.
- If reconciliation can't be proven clean within the grace window (the
  control channel itself fails, or the grace deadline elapses), the
  connection is **discarded**, never handed back to the pool in an
  ambiguous state.

From the caller's side, this all just looks like: cancelling either
throws `OperationCanceledException` promptly, or (rarely) the call
completes with a real result despite the cancellation — never a hang,
and never a connection silently corrupted for the next caller. See the
design doc's §2 for the full state machine if you need the exact
guarantees this makes.

## See also

- [Architecture](architecture.md) — why there are two pools at all.
- [Cluster](cluster.md) — the cluster-routed versions of both.
