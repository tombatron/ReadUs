# Architecture

This is the public-facing overview — what ReadUs is built out of and why,
for someone deciding whether to adopt it. For the exhaustive internal
design log (every state machine, every invariant, every bug found during
development and how it was fixed), see
[`docs/design/state-machines.md`](../design/state-machines.md).

## Two connection tiers, one reason: blocking commands

A single Redis connection processes commands strictly in order. If one
command blocks the server from replying (`BLPOP` with no data ready,
`WAIT`, ...), every other command queued behind it on that same connection
waits too — a client that multiplexes many concurrent callers over a
shared pool of connections (the usual approach for throughput) can have an
ordinary `GET` stuck behind someone else's multi-second `BLPOP` with no way
to tell them apart.

ReadUs avoids this with two pools per client, not one:

- **Tier 1 — the multiplexed pool.** A small, fixed-size set of
  connections shared by every ordinary (non-blocking) command. Multiple
  callers can have requests in flight on the same connection at once;
  replies are correlated back to the right caller purely by FIFO order
  (Redis guarantees replies come back in the order requests were sent).
  This is where the bulk of steady-state traffic goes, and it's built to
  do that with minimal allocation — no boxing, no intermediate object
  arrays for command arguments, buffers reused via `System.IO.Pipelines`.
- **Tier 2 — the leased pool.** A separate, smaller pool of connections
  handed out one-at-a-time via a lease (`ConnectionLease`) for the
  duration of a blocking command or a transaction, then returned. A slow
  `BLPOP` only ever blocks the one connection it's leasing — never Tier
  1's shared pool, and never another caller's own leased connection.

Both tiers ultimately run over the same `RedisConnection` type — one
socket, one read loop, one write loop, the same RESP framing and
completion-claim machinery either way. Tiering is a pooling/lifetime
policy layered on top of common connection machinery, not two separate
protocol stacks.

Cancelling a blocking command mid-wait is itself a real state machine:
ReadUs issues `CLIENT UNBLOCK <id> TIMEOUT` on a small side channel and
waits (bounded) for the primary connection to settle before deciding
whether the connection can be safely returned to the pool or must be
discarded — never leaving it in an ambiguous state where a caller can't
tell whether their command actually ran. See
[Transactions and blocking commands](transactions-and-blocking-commands.md)
for the caller-facing version of this, or the design doc's §2 for the full
state machine.

## RESP3 by default

ReadUs always negotiates RESP3 via `HELLO 3` during the connection
handshake. This isn't just a version bump — RESP3 changes how several
reply shapes are framed (maps and sets become their own types instead of
flat arrays; nulls have a dedicated type instead of a special-cased empty
array/bulk-string), and, more importantly, adds an out-of-band **push**
frame type. Client-side caching invalidations and Pub/Sub messages both
arrive as push frames on the same connection that's doing ordinary
request/reply traffic — no second dedicated connection needed the way
RESP2 clients require for Pub/Sub-style delivery. `RedisResult`'s `Type`
property models every one of these RESP3 shapes directly (see
[Getting started](getting-started.md#redisresult)), not just the RESP2
subset.

## The command-table source generator

Redis's own command metadata (`src/commands/*.json` from the Redis
project itself, vendored under `codegen/redis-commands/`, currently
pinned to the `8.10.1` tag — see that directory's `SOURCE.md` for exact
provenance) is read at **ReadUs's own build time** by a Roslyn incremental
source generator (`ReadUs.SourceGenerators`), which emits:

- A typed extension method per command with a well-behaved argument shape
  — optional/flag arguments become named C# parameters (`conditionNx:
  true`), not a loose `object[]`.
- Full routing/blocking/key-position metadata for *every* vendored
  command, whether or not it got a typed method (this is what
  `ClusterClient` uses to route a command by key without needing its own
  hand-maintained table).

Two real limits, by design rather than oversight:

- Only two levels of argument nesting are modeled (a plain top-level
  argument, or one level of a `oneof`/`block` group beneath it). A command
  whose shape goes deeper than that still gets full metadata (so Cluster
  routing and blocking detection still work), just no generated typed
  method — reach it through the raw `ExecuteAsync` escape hatch instead.
  This covers 412 of 459 vendored commands (~90%) as of the `8.10.1`
  snapshot.
- A handful of commands (`SORT`'s `BY`/`GET`/`STORE`, and other
  "movable keys" commands) have key positions Redis itself only resolves
  server-side. Cluster routing uses whatever key positions *are*
  statically known and accepts that a wrong first guess surfaces as an
  ordinary `MOVED` reply, which is followed transparently rather than
  reimplemented client-side.

The same generator pattern (a second, independent incremental generator)
also powers `ReadUs.Extensions.Hashes`'s zero-reflection POCO↔Hash
mapping — see [Hash and JSON mapping](hash-and-json-mapping.md).

## The zero-allocation goal

The steady-state command path — write a command, await its reply — is
built to avoid allocating on the hot path: command arguments are written
directly into pooled buffers via `System.IO.Pipelines`
(`RespCommandWriter`), replies are correlated to requests via a pooled
free-list of reusable awaiter objects (`PendingRequest`), and a
`RedisResult`'s scalar payload is copied out of the connection's read
buffer exactly once, at the point it has to cross out of the read loop
(unavoidable — the buffer itself is about to be recycled). This is a goal
for the *steady-state* command path specifically, not an absolute rule
everywhere: the comparatively rare blocking-command cancellation path, for
instance, does allocate a reconciliation object, deliberately, because
that path's correctness matters far more than its allocation count. See
[`docs/benchmarks.md`](../benchmarks.md) for what this actually measures
out to.

## See also

- [`docs/design/state-machines.md`](../design/state-machines.md) — the
  full internal design log this overview is distilled from.
- [Cluster](cluster.md) / [Sentinel](sentinel.md) — how routing and
  failover build on top of this same connection layer.
