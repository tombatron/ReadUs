# ReadUs — Core State Machine Specifications

Status: **draft v0.1** — written before any connection code, per the project mandate
that concurrency-critical state machines are specified before they are implemented
(spec §9, §13 step 1).

This document currently covers, in full rigor, the two machines the mandate calls
out as most failure-prone and required before writing connection code:

1. [Connection Lifecycle](#1-connection-lifecycle-state-machine)
2. [Blocking-Command Cancellation Race](#2-blocking-command-cancellation-race)

It also sketches, at outline depth only, the two machines whose full spec is
deferred to their own project phases (§13 steps 5+):

3. [Cluster Redirect / Topology Refresh (outline)](#3-cluster-redirect--topology-refresh-outline)
4. [Sentinel Failover (outline)](#4-sentinel-failover-outline)

Every machine here is meant to be precise enough that a reviewer — or the
exhaustive state-space walker described in §2.6 — can look at a trace of
`(state, event)` pairs and say definitively whether it is legal. If an
implementation detail changes the legal transition set, this document changes
first, in the same PR.

---

## Conventions

- **State** — a named, mutually exclusive condition of the entity being modeled.
  An entity is in exactly one state at any instant, and that state is owned by
  exactly one writer (never inferred by racing readers).
- **Event** — something that happens to the entity: an I/O completion, a caller
  action (cancel, dispose), a timer firing, a reply parsed off the wire.
- **Transition** — `State --event--> State'`. Anything not listed is illegal;
  hitting an unlisted `(state, event)` pair in the walker or in production is a
  bug, not a "shouldn't happen."
- **Invariant** — a property that must hold across *every* reachable state, not
  just at rest.
- **Hazard** — a specific bad outcome the design must make structurally
  impossible (not just unlikely). Hazards map 1:1 to assertions the exhaustive
  walker checks after every driven trace.

---

## 1. Connection Lifecycle State Machine

This machine describes a single physical connection — one TCP (optionally
TLS-wrapped) socket to one Redis node, running one write loop and one read
loop over `System.IO.Pipelines`. It is the *same* machine for a Tier 1
(multiplexed pool) member and a Tier 2 (leased/dedicated) connection — see
§4 of the project spec. Tiering is a pooling/lifetime *policy* layered on top
(who is allowed to enqueue work, and when the connection is handed back);
the socket-level FSM underneath does not know or care which tier owns it.

### 1.1 States

| State | Meaning |
|---|---|
| `Created` | Object exists, no I/O has started. |
| `Connecting` | TCP connect (and DNS resolution, per §7 — resolved fresh every time, never cached across reconnects) in flight. |
| `TlsHandshaking` | Only entered if TLS is configured. `SslStream` handshake in flight, including certificate validation callback and optional mutual-TLS client cert presentation. |
| `ProtocolHandshake` | `HELLO` sent, negotiating RESP2 vs RESP3, reading server capabilities. |
| `Authenticating` | Credential material (static password, or a value pulled from `ICredentialsProvider`, §7) is being applied via `AUTH`/`HELLO ... AUTH`. |
| `Ready` | Fully initialized. Read loop and write loop are both running. The connection may accept new outbound commands (subject to tier policy) and will deliver replies in strict FIFO order. |
| `Draining` | A graceful shutdown has been requested (pool eviction, client `DisposeAsync`, forced reconnect due to DNS/topology change). No *new* work is accepted; in-flight requests already written are allowed to complete, up to a bounded drain deadline. |
| `Faulted` | An unrecoverable transport or protocol error was observed (socket exception, TLS failure, RESP frame desync, unexpected EOF, missed heartbeat). The connection is dead but not yet torn down. |
| `Closed` | Terminal. Socket disposed, all queues drained, no further transitions possible. |

### 1.2 Transition table

```
Created           --Open()-->                          Connecting
Connecting        --tcp connected, TLS configured-->    TlsHandshaking
Connecting        --tcp connected, no TLS-->            ProtocolHandshake
Connecting        --connect timeout / refused / DNS failure-->  Faulted

TlsHandshaking    --handshake ok-->                     ProtocolHandshake
TlsHandshaking    --cert rejected / handshake timeout--> Faulted

ProtocolHandshake --HELLO ok, auth required-->          Authenticating
ProtocolHandshake --HELLO ok, no auth required-->       Ready
ProtocolHandshake --HELLO rejected / malformed reply--> Faulted

Authenticating    --AUTH ok-->                          Ready
Authenticating    --AUTH failed (WRONGPASS/NOPERM/provider threw)--> Faulted

Ready             --graceful close requested-->         Draining
Ready             --I/O exception (read or write loop) / protocol desync / peer FIN-->  Faulted
Ready             --missed heartbeat threshold-->       Faulted

Draining          --all in-flight completed OR drain deadline elapsed-->  Closed
Draining          --I/O exception while draining-->     Faulted   (see 1.4)

Faulted           --cleanup complete (all pending awaiters failed, socket disposed)--> Closed

Closed            --（terminal, no outbound transitions）--
```

ASCII overview:

```
 Created -> Connecting -> [TlsHandshaking] -> ProtocolHandshake -> Authenticating -> Ready -> Draining -> Closed
                |                |                    |                  |            |          |
                +----------------+--------------------+------------------+------------+----------+--> Faulted -> Closed
```

### 1.3 Re-authentication is *not* a new state

Live credential rotation (§7 — AWS IAM tokens, Azure Entra tokens rotating
under a long-lived connection) is modeled as an ordinary pipelined command
sent while already `Ready`: the new `AUTH`/`HELLO AUTH` frame is written and
its reply consumed like any other request, in the same FIFO order as
everything else on that connection. It does **not** detour through
`Authenticating`. Two outcomes:

- Re-`AUTH` succeeds → stay in `Ready`, nothing else observes this happened.
- Re-`AUTH` fails → treated as an ordinary command-level error, not a
  connection fault. The credential-rotation policy layer (outside this FSM)
  decides whether to retry, or to force a `Draining`→reconnect cycle with the
  refreshed credentials pulled fresh from the `ICredentialsProvider`. This
  keeps the core FSM small; "reconnect with new credentials" is just another
  path into `Ready --> Draining`.

### 1.4 Why `Draining` can still reach `Faulted`

A graceful close can race with the socket dying for unrelated reasons (peer
resets the connection while we're waiting for in-flight replies to drain).
`Draining --I/O exception--> Faulted` exists so that the *same* cleanup path
(fail all still-pending awaiters, exactly once each, then dispose) is used
whether the connection died gracefully or violently. There is deliberately no
`Draining -> Closed` shortcut that skips awaiter accounting — every path to
`Closed` passes through the accounting step described in Invariant I4 below.

### 1.5 Invariants

- **I1 — Single owner.** State transitions are performed by a single logical
  owner per connection (in the implementation: an `Interlocked`-guarded state
  field or a single-threaded state-transition method never re-entered
  concurrently). No thread ever observes a state, decides based on it, and
  writes a new state without that decision+write being atomic with respect to
  other potential writers.
- **I2 — No writes after death.** Once a connection is `Faulted` or `Closed`,
  no byte is ever written to its socket again, by any caller, under any
  policy. (A caller that raced into "send" just as the connection faulted
  must get a synchronous rejection, not a write into a dead pipe.)
- **I3 — Exactly-once completion.** Every request accepted for write while
  `Ready` receives **exactly one** completion — success, RESP-level error, or
  connection-fault exception. Never zero (lost wakeup), never two (double
  completion of a pooled `IValueTaskSource`).
- **I4 — Fault drains the queue.** The transition *into* `Faulted` must, as
  part of that same transition (not eventually, not best-effort), walk every
  outstanding awaiter for that connection in original FIFO order and complete
  each exactly once with a well-defined `RedisConnectionException`. This is
  what makes I3 provable rather than "usually true." Extended by I8: this
  walk covers both the FIFO reply-tracking queue *and* the write loop's own
  not-yet-claimed work queue, since a request can be waiting in either one
  depending on exactly when the fault happened.
- **I5 — Clean-boundary pool return.** A connection is only handed back to a
  Tier 2 pool (or left in the Tier 1 rotation) from `Ready`, and only when the
  protocol layer can *prove* — via byte-accounting in the RESP reader, not by
  assumption — that the previous reply was fully consumed and the read
  position sits exactly on the next frame boundary. "Probably fully read" is
  not suffient; see the pool-poisoning hazard (H3) in §2.5, which is really a
  cross-cutting hazard between both machines.
- **I6 — Fresh DNS per connect.** `Connecting` always re-resolves the
  configured endpoint name; no IP is cached across a `Faulted`/`Draining` →
  reconnect cycle (§7 — managed endpoints rotate IPs on failover).
- **I7 — Single active writer.** At most one execution context is ever
  inside `RespCommandWriter.WriteCommand`/`PipeWriter.FlushAsync` for a
  given connection at a time. Enforced structurally, not by a lock: one
  dedicated write loop (symmetric to the read loop already named in this
  section's opening paragraph) is the *only* code path that ever touches
  the connection's `PipeWriter`, for the connection's whole lifetime.
  Every other caller only ever enqueues work for that loop to pick up.
- **I8 — No write-queue stranding.** A request accepted for write (queued
  for the write loop, before I3's "accepted for write while Ready" moment
  is even reached) is guaranteed to eventually reach the point I3/I4
  govern — either the write loop's own drain claims it (moving it into
  the same FIFO-tracked structure I4 already walks) before attempting to
  write it, so a mid-batch write failure still leaves it correctly
  tracked for I4's drain; or, if it's still sitting unclaimed when the
  connection faults, I4's drain is extended to also walk the write queue
  directly. Together these mean nothing can be accepted and then silently
  dropped between "enqueued" and "written."

---

## 2. Blocking-Command Cancellation Race

This is the state machine for a *single logical request* riding on top of a
`Ready` Tier 2 leased connection, specifically for `BLOCKING`-flagged commands
(`BLPOP`, `BLMPOP`, blocking `XREAD`, `WAIT`, etc. — full list from the §3
codegen metadata). It is the sharpest correctness risk in the whole project:
a caller's `CancellationToken` firing is fundamentally racing a Redis server
that may be about to reply anyway.

### 2.1 Actors

- **Caller** — application code awaiting the operation, holding a
  `CancellationToken` and/or a client-side deadline.
- **Primary connection** — the leased Tier 2 connection the blocking command
  was sent on.
- **Control connection** — a small, dedicated sub-pool (never drawn from the
  general Tier 2 pool — see Hazard H5) used to send `CLIENT UNBLOCK <id>` on
  a side channel while the primary connection is mid-block.
- **Server** — the Redis node, which may unblock the primary connection on
  its own (real data arrived / command's own timeout elapsed) at any moment,
  independent of and concurrent with the client's cancellation attempt.

### 2.2 Precondition: client ID capture

Every leased connection, immediately upon reaching `Ready` (§1), issues
`CLIENT ID` once and caches the result for the lifetime of that connection.
This must happen *before* the connection is eligible to run a blocking
command, so cancellation never needs an extra round trip (which would itself
be racing the thing it's trying to interrupt) to discover which server-side
client to unblock.

### 2.3 Per-request states

| State | Meaning |
|---|---|
| `Idle` | Not yet sent. |
| `Sent` | Command bytes fully written to the primary connection; the server may or may not have entered its blocking wait yet. |
| `Cancelling` | Caller's token fired or deadline elapsed; we won the claim (§2.4) to attempt cancellation and are about to contact the control connection. |
| `UnblockRequested` | `CLIENT UNBLOCK <id> TIMEOUT\|ERROR` sent on the control connection; awaiting its reply. |
| `ReconcilingPrimary` | Waiting, under a short bounded grace deadline, for the corresponding frame to arrive on the *primary* connection after a successful unblock. |
| `CompletedNormal` | A genuine data reply (or the command's own server-side nil-timeout) was consumed from the primary connection and delivered to the caller. Primary connection returns to pool. |
| `CompletedCancelled` | The operation is conclusively known to have been cancelled, the corresponding unblock frame was consumed cleanly. Primary connection returns to pool. |
| `Discarded` | Reconciliation could not be proven safe (control channel failed, unblock timed out, grace deadline elapsed with no frame). Primary connection is **closed**, not pooled. Caller sees `OperationCanceledException`. |

### 2.4 The completion claim (how H1/H2 are structurally prevented)

Exactly one path is ever allowed to call `SetResult`/`SetException` on the
caller-facing `ValueTask` for a given request. This is enforced with a single
`Interlocked.CompareExchange` on a per-request enum field
(`NotCompleted -> Completing`), *not* by convention:

- The **read loop**, on parsing a complete reply frame that belongs to this
  request, attempts the CAS. If it wins, it completes the caller with the
  parsed result and the request is done — full stop, regardless of whatever
  the cancellation path is doing concurrently.
- The **cancellation path**, on the token firing, attempts the *same* CAS
  before doing anything else observable. If it loses (read loop already
  claimed), cancellation is a no-op: **a value that already arrived is
  delivered to the caller even though cancellation was requested.** This is
  a deliberate, documented policy choice — cancellation here is
  best-effort-to-stop-the-server-side-block, not a guarantee that no result
  will ever be observed after the token fires. If it wins, the request moves
  `Sent -> Cancelling` and only *then* does the side-channel work begin.

Because the claim is won before any side-channel I/O starts, there is no
window where both paths believe they are still eligible to complete the
caller.

### 2.5 Transition table

```
Idle              --write complete-->                        Sent

Sent              --read loop parses matching frame first
                    (wins completion claim)-->                CompletedNormal

Sent              --cancellation fires, wins completion claim--> Cancelling

Cancelling        --CLIENT UNBLOCK sent on control conn-->    UnblockRequested

UnblockRequested  --control conn returns 1 (was blocked)-->   ReconcilingPrimary
UnblockRequested  --control conn returns 0 (already had a
                    reply in flight; primary frame is real data)-->  ReconcilingPrimary
                    (grace wait; see 2.4 — if the primary frame turns out to be
                    the genuine data reply, deliver it as CompletedNormal)
UnblockRequested  --control conn errors / times out-->        Discarded

ReconcilingPrimary --expected frame consumed within grace deadline,
                     unblock artifact (nil/err per UNBLOCK mode)-->  CompletedCancelled
ReconcilingPrimary --expected frame consumed within grace deadline,
                     turns out to be genuine data-->           CompletedNormal
ReconcilingPrimary --grace deadline elapses with nothing consumed--> Discarded
```

### 2.6 Hazards (= walker assertions)

The exhaustive state-space walker (implemented alongside the Tier 2 pool in
§13 step 3, in `ReadUs.Tests.Unit`) drives the real implementation — not a
model of it — through every reachable interleaving of `{cancel fires, reply
byte arrives, control-connection reply arrives, grace deadline elapses}` and
asserts, after every single trace, that none of the following ever occurred:

- **H1 — Lost wakeup.** The caller's `ValueTask` never completes at all
  (both the read loop and the cancellation path decline to claim, e.g. due to
  an off-by-one in the CAS). *Structural prevention:* the CAS in §2.4 has
  exactly two callers and both are on the mandatory path of "a reply frame
  arrived" or "cancellation fired" — there is no third way to become
  uncompleted forever.
- **H2 — Double completion.** Both paths call `SetResult`/`SetException`.
  *Structural prevention:* the CAS itself; the loser is architecturally
  incapable of reaching the completion call.
- **H3 — Pool poisoning.** A connection is returned to the pool while a
  frame belonging to this request (or the unblock artifact) is still unread
  or partially read. *Structural prevention:* the *only* transitions that
  return a connection to the pool are `CompletedNormal` and
  `CompletedCancelled`, and both are only reachable after a full frame was
  demonstrably consumed by the RESP reader (Invariant I5). `Discarded` never
  pools the connection — it closes it, trading a connection for correctness.
- **H4 — Deadlock between cancellation and normal completion.** The
  cancellation path blocking on something the read loop needs to make
  progress (e.g., both wanting the same exclusive lock in incompatible
  order). *Structural prevention:* the read loop never acquires any lock
  owned by the cancellation path; it only participates in the lock-free CAS
  and otherwise always keeps consuming bytes off the wire into its internal
  buffer regardless of completion-claim outcome. Claiming decides *who
  delivers the result*, never *whether bytes get read*.
- **H5 — Control-channel starvation.** The `CLIENT UNBLOCK` side channel is
  itself exhausted/blocked (e.g., drawn from the same pool as the blocking
  commands it's meant to interrupt), so cancellation can never be delivered
  under load. *Structural prevention:* the control connection pool is a
  separate, small, always-reserved sub-pool used exclusively for control
  commands (`CLIENT UNBLOCK`, health-check `PING`), never leased for
  ordinary application commands.

### 2.7 Timeout composability

Per project spec §4: the command's own server-side timeout argument (e.g.
`BLPOP key 5`) and the client-side `CancellationToken`/deadline are
independent and both must be honored. This machine treats a client-side
deadline elapsing identically to a `CancellationToken` firing — it is just
another producer of the `Sent -> Cancelling` transition. The server-side
timeout is not special-cased at all in this FSM: when it elapses, the server
sends its own nil/timeout reply, which the read loop parses and claims via
the ordinary `Sent -> CompletedNormal` transition, exactly like a real data
reply. From the client's perspective a "server gave up waiting" reply and "a
value was popped" reply differ only in payload, not in state-machine path.

---

## 3. Cluster Redirect / Topology Refresh (outline)

Full spec deferred to §13 step 5. Recorded now only so the shape is not
forgotten and so nothing in Tier 1/Tier 2 accidentally forecloses it.

States (per logical multi-node command dispatch, layered on top of whichever
connection — Tier 1 or Tier 2 — the command would have used against a
standalone node):

`Routing -> Dispatched -> {Completed, Moved, Asked, TryAgain, ClusterDown, Faulted}`

- `Moved` triggers: update local slot map for at least the redirected slot,
  schedule a background full topology refresh (`CLUSTER SHARDS`), retry
  against the new owner, with a redirect-count cap per logical call.
- `Asked` triggers: send `ASKING` then the original command to the *named*
  target, one-shot — never persisted into the slot map.
- `TryAgain` triggers: bounded backoff, retry same node.
- `ClusterDown` is surfaced as a distinct exception type from ordinary
  connection failure and gets a longer backoff.
- `CROSSSLOT` is treated primarily as a pre-flight client bug to catch before
  the command is ever sent (slot computed for every key argument up front,
  including `MOVABLEKEYS` commands, using the same generated metadata as §3 of
  the project spec) — the wire-level error path exists only as a defensive
  fallback, not the primary mechanism.

Open question to resolve when this phase starts: whether `Moved`/`Asked`
retry-dispatch reuses the *same* request object (rewriting destination) or
allocates a new one — the allocation-budget mandate in §8 argues for reuse,
but reuse interacts with the completion-claim pattern from §2.4 and needs its
own hazard analysis before being declared safe.

---

## 3.1 Cluster Node Health Tracking (outline)

Closes a gap left open, deliberately, by §3's original implementation (see
the "Recorded during §13 step 5" entry in §5): a node that goes down
surfaces as an ordinary `RedisConnectionException` from whatever call hit
it, with no tracking of *repeated* failure and no distinct "this node is
known-bad, stop sending it traffic" state. Written as an outline, in the
same spirit as §3/§4, before implementation — this is new state-machine
surface, not a bug fix to existing surface.

States (per cluster node, keyed by its endpoint — independent of, and
layered above, that node's own `RedisClient`/Tier 1 pool, which already
self-heals its individual connections per §1; this machine is about whether
the *cluster layer* keeps routing to a node at all):

`Healthy -> Quarantined -> Healthy` (loops)

- `Healthy -> Quarantined`: a fixed number of *consecutive*
  `RedisConnectionException`s observed for that node (an ordinary Redis
  error reply — `MOVED`/`ASK`/`TRYAGAIN`/`CLUSTERDOWN`/anything else — does
  *not* count; the node responded, it's reachable, it's healthy). Any
  intervening success resets the counter to zero.
- While `Quarantined`: every call that would route to this node fails fast
  with a distinct exception, *without* attempting the network — no caller
  traffic is used to probe recovery. Reasoning: a slot-owning master has no
  alternative target (replica-read routing is separately out of scope, §5),
  so "retry the same dead node on every caller's timeline" is exactly the
  wasted-latency behavior this machine exists to avoid. Reintegration is
  the background prober's job, on its own schedule, not the foreground
  path's.
- `Quarantined -> Healthy`: a dedicated background prober (one per
  quarantined node, started the moment it's quarantined) sends a bare
  `PING` through that node's own `RedisClient` on an exponential
  backoff-with-jitter schedule (mirrors Tier 1's own
  `MultiplexedConnectionPool` reconnect loop exactly, both in shape and in
  starting/max backoff constants). First successful `PING` clears the
  state and the failure counter and the prober loop exits; a failed `PING`
  just widens the backoff and tries again. Only the prober can transition a
  node back to `Healthy` — an ordinary foreground caller's success while a
  node happens to be quarantined only resets *that node's failure counter*
  (harmless either way, since the counter is meaningless once
  `Quarantined`), it does not itself clear quarantine.
- The round-robin fallback used for keyless/admin commands (and for a slot
  the local map doesn't cover) skips a quarantined master in favor of a
  healthy one, the same skip-ahead shape as Tier 1's `Rent()`. If *every*
  known master is quarantined, the fallback returns one anyway — matching
  Tier 1's own "hand back a connection known to be dead so the caller fails
  fast" fallback rather than blocking.
- The periodic (and MOVED-triggered) full topology refresh tries known
  masters in health order, quarantined ones last, so it doesn't spend its
  one attempt-that-matters against a node already known to be down.

Not covered by this machine, deliberately: per-node request-level circuit
breaking with a half-open "let one trial call through" state (rejected in
favor of the simpler hard fail-fast-plus-dedicated-prober shape above,
which needs no shared state between foreground and background paths to
reason about); ejecting/closing a quarantined node's already-open Tier 1
pool connections (they're left alone — Tier 1's own health loop already
manages them, and there's no correctness reason to force-close a
connection that happens to still be usable for the prober's own `PING`).

---

## 3.2 Cluster Replica Read Routing (outline)

Project spec §5: "Read routing policy should be configurable per call or
per client: primary-only (default, safest), prefer-replica, replica-only,
or a round-robin/latency-aware policy across primary+replicas for a
shard." Also §10: read-preference overrides live behind "clearly separate,
opt-in surface" — never change what an ordinary `ExecuteAsync` call does.

**Surface**: a new `ExecuteAsync(commandName, args, ReadPreference, ct)`
overload, opt-in per call. The existing `ExecuteAsync(commandName, args,
ct)` is unchanged — it now just forwards to the new overload with
`ReadPreference.PrimaryOnly`, so every existing caller and test keeps
today's exact behavior with zero risk. No client-wide default is added
this pass (spec says "per call *or* per client" — per-call alone already
satisfies it; a client-wide default is easy to add later if wanted).
~~No client-wide default is added this pass~~ Added in a later pass: a
`defaultReadPreference` parameter on `ClusterClient.ConnectAsync`
(default `PrimaryOnly`, so existing callers see no change), applied
automatically by the plain `ExecuteAsync` overload — but only to a
command the vendored table marks read-only. A write always stays
`PrimaryOnly` regardless of the configured default; otherwise setting
any non-primary default would make every ordinary write through the
plain overload throw the CROSSSLOT-style validation below. The per-call
`ReadPreference` overload is unaffected and still always wins when used.

**Client-side validation, same shape as CROSSSLOT (§3)**: a non-`PrimaryOnly`
preference on a command that isn't read-only (`GeneratedCommandInfo
.IsReadOnly`, from the vendored table — the same metadata `ExtractKeys`
already uses) is rejected client-side with `ArgumentException` before
anything is sent, rather than letting the server's own `-READONLY` error
surface it after the fact.

**Candidate selection per read preference**, given the shard (primary +
its replicas) that owns the command's slot:

- `PrimaryOnly`: the primary. Always. (No behavior change from before this
  feature existed.)
- `PreferReplica`: a healthy replica if the shard has one; falls back to
  the primary if it doesn't, or if every replica is currently quarantined
  (§3.1's tracker — replicas are tracked exactly like masters).
- `ReplicaOnly`: a healthy replica, or throws `RedisConnectionException`
  if none exists or all are quarantined — deliberately does *not* fall
  back to the primary, since a caller who asked for replica-only
  presumably has a reason (e.g. explicitly avoiding primary load) that a
  silent fallback would violate.
- `RoundRobin`: round-robins across {primary, all replicas} for that
  shard, skipping quarantined candidates, reusing the same round-robin
  counter and "just hand back a quarantined one anyway if every candidate
  is quarantined" fallback shape as the existing keyless-command routing.

**`READONLY`/`READWRITE` (spec's own requirement)**: a replica connection
must have sent `READONLY` before Redis will serve reads from it. Managed
via a new, general-purpose `RedisConnectionOptions.PostConnectAsync` hook
(project spec §7 layer, not cluster-specific) that
`RedisConnection.ConnectAsync` runs once per physical connection right
after it reaches `Ready` — which means it fires for *every* connection
made with those options, including a pool's self-healing reconnect
replacement, not just the first. Cluster composes this hook (via a `with`
expression — the reason `RedisConnectionOptions` became a `record` this
pass) onto whichever options the caller's own factory returns, only for
node endpoints this client is treating as a replica target; a plain
master's options are never touched. This keeps `READONLY` entirely inside
the dedicated-vs-pooled connection lifecycle, exactly as the spec asks —
nothing about it is visible in the public API. `READWRITE` (undoing
`READONLY`) is not needed: a replica-designated `RedisClient` here is
*only* ever used for reads (the `IsReadOnly` gate above guarantees that),
so there is never a reason to flip it back.

**Topology**: `ClusterTopology` already discards replica nodes from
`CLUSTER SHARDS` (see the original §13 step 5 "Recorded" entry) — this
phase stops discarding them, tracking each shard's replica list alongside
its master. A `MOVED`-triggered single-slot patch (`WithSlotOverride`)
still only knows the new primary's endpoint, so the patched sub-range gets
an empty replica list until the next full background refresh fills it in
— the same "surgical patch now, fuller refresh in the background" tradeoff
the original redirect design already made for the primary case.

**Keyless commands** (an admin command, or a slot the local map doesn't
cover) always stay primary-only-round-robin regardless of the requested
`ReadPreference` — there's no single shard to pick a replica "for" in that
case, so `ResolveEndPoint`'s existing masters-only fallback is left
untouched rather than generalized to "any node, master or replica, across
the whole cluster."

Not covered by this pass, deliberately: ~~a client-wide default read
preference~~ (see "Surface" above — added in a later pass); latency-aware replica selection
(`RoundRobin` is plain round-robin, not the "or latency-aware" alternative
the spec offers — no latency signal exists to rank candidates by yet, and
adding one speculatively without a benchmark showing it matters would
contradict §8's own "don't add intrinsics/complexity speculatively"
stance).

---

## 4. Sentinel Failover (outline)

Full spec deferred to §13 step 5. Recorded now for the same reason as §3.

States (per watched master/service name):

`Discovering -> Connected(master) -> {FailoverDetected, MasterUnreachable} -> Discovering`

- `Discovering`: query configured sentinels for
  `SENTINEL get-master-addr-by-name`; requires a documented quorum/majority
  acceptance policy across sentinels queried (not "trust the first
  responder") before transitioning to `Connected`.
- `Connected(master)`: normal operation; a dedicated pub/sub connection to a
  quorum-aware subset of sentinels listens for `+switch-master` for the
  watched name as the primary failover signal, with periodic
  `SENTINEL get-master-addr-by-name` polling as a safety net in case the
  pub/sub connection itself silently drops.
- `FailoverDetected` (from either the pub/sub push or the polling net) tears
  down the current master's Tier 1/Tier 2 connections and re-enters
  `Discovering` against the newly announced address.
- The exact quorum policy (how many sentinels must agree, how disagreement
  during the failover window is resolved) is explicitly called out in the
  project spec as needing a written, testable policy rather than incidental
  behavior — to be nailed down when this phase starts, not inferred from
  whatever the first implementation happens to do.

---

## 5. Deferred decisions worth revisiting later

Tracking per §11 of the project spec — places where a call was made now that
should be easy to revisit rather than silently baked in:

- The completion-claim mechanism (§2.4) is specified as a single
  `Interlocked.CompareExchange` on a per-request enum. This is deliberately
  the simplest thing that satisfies H1/H2; if profiling later shows
  contention (unlikely at FIFO-per-connection scale, but not proven yet), a
  lock-free ring buffer of claim slots is the fallback — the *contract*
  (exactly-once claim, decided before any side-channel I/O) is what other
  code should depend on, not the specific primitive.
- The control-connection sub-pool (H5) is specified as "small and always
  reserved" without a fixed size yet — sizing policy (fixed count vs.
  fraction of Tier 2 pool size) is an implementation-phase decision.
- Re-authentication-on-failure policy (§1.3: retry re-`AUTH` vs. force
  reconnect) is left to the credential-rotation layer rather than fixed here,
  intentionally, since AWS IAM vs. Azure Entra token providers may want
  different defaults.

### Recorded during §13 step 3 implementation (Tier 2 + blocking commands)

- **Grace deadline is a fixed constant, not configurable yet.**
  `RedisConnection.UnblockReconciliationGrace` (2s) bounds how long a
  cancelled blocking command waits, after a successful `CLIENT UNBLOCK`, for
  the corresponding frame before the connection is discarded (§2.5's
  `Discarded` transition). Should become part of `RedisConnectionOptions`
  once Tier 2 pool configuration solidifies; hardcoding it now was the
  simplest thing that let the reconciliation logic be written and tested.
- **Control channel is a narrow interface (`IControlChannel`), not a direct
  dependency on the pooling layer.** `RedisConnection.SendBlockingAsync`
  takes an `IControlChannel` rather than referencing
  `ReadUs.Pooling.MultiplexedConnectionPool` directly, so the Connections
  layer doesn't need to know about pooling policy. `MultiplexedConnectionPool`
  satisfies it structurally. Worth keeping as the pattern scales (e.g. if the
  control channel ever needs its own retry/backoff policy independent of
  ordinary Tier 1 traffic).
- **The reconciliation sink (`TaskCompletionSource`) is an allocation on the
  cancellation path.** Fine per §8's own framing — the zero-allocation
  mandate is for steady-state command execution, not for the comparatively
  rare blocking-cancellation path — but flagged here so it isn't mistaken for
  an oversight if it shows up in an allocation profile.
- **The exhaustive walker (§2.6, §9.1) covers the `PendingRequest` completion-
  claim primitive exhaustively (every reachable ordering of claim/reply/
  reconciliation events, both as deterministic sequences and as a real
  concurrent-thread stress test), but does not yet model the full network-
  timing state space** (control-channel latency vs. primary-channel latency
  vs. grace-deadline expiry, all racing against a live server). That's
  integration-level coverage — `BlockingCommandTests` in
  `ReadUs.Tests.Integration` exercises one real end-to-end path against a
  live server — and will grow into deliberate fault injection (delayed/
  dropped bytes on each channel independently) once the Testcontainers-based
  harness lands with Cluster/Sentinel (project spec §9.2), rather than being
  faked with a mocked transport now.
- **`LeasedConnectionPool` is fixed-size with lazy self-healing, not
  eagerly-reconnecting.** A connection returned in a non-`Ready` state is
  discarded and replaced the next time it would have been leased, not
  reconnected in the background. Simpler for v0; a background top-up could
  reduce tail latency for the unlucky caller who pays for the reconnect, if
  that turns out to matter.

### Recorded during §13 step 4 implementation (command-table source generator)

- **Command/subcommand names are PascalCased as whole words, not split into
  natural word boundaries** (`BLPOP` → `Blpop`, not `BlPop`) — there's no
  general way to do this correctly without a hand-curated exception table for
  all ~460 commands, which would itself be an ad hoc, drifting artifact. A
  literal hyphen in a name (`NO-EVICT`) *is* treated as a real word boundary
  (→ `NoEvict`) since that's an unambiguous, mechanical split. Revisit if a
  curated override table (only for the handful of commands where the ugly
  casing really bothers people) turns out to be worth the upkeep.
- **A `oneof` group's members are flattened into independent optional
  parameters, prefixed with the group's own name** (EXPIRE's `condition`
  oneof → `conditionNx`/`conditionXx`/...). This was forced by BLMOVE having
  two independent oneof groups (`wherefrom`/`whereto`) that both offer
  `LEFT`/`RIGHT` — without the prefix the flattened parameters collide. A
  side benefit: the prefix also makes clear which logical group a flag
  belongs to. The server still enforces mutual exclusivity within a oneof;
  this client doesn't duplicate that validation client-side.
- **Only two levels of argument nesting are supported**: a plain top-level
  argument, or one level of `oneof`/`block` beneath it. Anything deeper (a
  `block` containing a non-scalar member, a `oneof` nested inside another
  `oneof`/`block`) aborts typed-method generation for that whole command —
  it still gets a full metadata entry (routing/blocking/key-spec
  information), just no generated typed method, until the escape hatch
  (`ExecuteAsync`/`ExecuteBlockingAsync`) is used directly. In practice this
  covers 412 of 459 vendored commands (~90%) as of the `8.10.1` snapshot.
- **Generated methods target `RedisClient` only** (as extension methods),
  not `ConnectionLease`/`RedisTransaction`, which run over a leased
  connection rather than the Tier 1/Tier 2 pools `RedisClient` wraps. This is
  why `RedisTransaction` still hand-writes its `WATCH`/`MULTI`/`EXEC`/
  `DISCARD` calls via `CommandNames` rather than using the generated
  `TransactionsCommands` extension methods. Extending codegen to also target
  a leased-connection context is future work, not fundamental — it would
  need a shared "can execute a command" abstraction that both `RedisClient`
  and `ConnectionLease` implement.
- **Response typing stays at `RedisResult` for every generated method** —
  the generator's scope this pass is typed *arguments*, not mapping each
  command's `reply_schema` to a precise C# return type. That's a
  substantially bigger design task (union-shaped replies, e.g. SET's
  `anyOf` of `OK`/previous-value/null, would need their own modeling) better
  done as a deliberate follow-up once the argument side has proven itself.
- **The generated per-call argument list is always built via a
  `List<ReadOnlyMemory<byte>>` + `ToArray()`, even for commands with zero
  optional/multiple parameters** (which could instead emit a fixed-size
  array literal directly). Kept uniform for generator simplicity; revisit if
  benchmarking the generated convenience layer itself (as opposed to the
  underlying `RespCommandWriter`/pipe path, which is already allocation-
  conscious) shows this matters.

### Recorded during §13 step 5 implementation (Cluster support)

- ~~**Replica read routing is not implemented.**~~ Done — see §3.2.
- ~~**No per-node health tracking, quarantine, or ejection.**~~ Done — see
  §3.1.
- ~~**No explicit per-node pipeline batching.**~~ Done — see "Recorded
  during implementation of batch execution" below.
- ~~**`ConcurrentDictionary<string, Task<RedisClient>>.GetOrAdd` can race
  under contention**~~ Fixed (see "Recorded during implementation of §3.2"
  below — done alongside replica read routing since that work already
  touched `GetOrCreateNodeClientAsync` and added a second caller of the
  same pattern, `GetOrCreateReplicaClientAsync`): `_nodeClients` now stores
  `Lazy<Task<RedisClient>>`, exactly the fix this bullet already named as
  the option, closing the discard-and-leak race for good rather than
  leaving it as a "matters if it ever shows up" gap.
- **Key extraction for the raw `ExecuteAsync` escape hatch only looks at
  top-level (non-subcommand) `CommandMetadata` entries.** Subcommands
  (`CLIENT`/`CLUSTER`/`SENTINEL`/...) never carry keys in the vendored table,
  so this is correct today, but it's a real assumption, not a general
  solution — if some future command ever added a subcommand with keys, it
  would silently route as if keyless (to an arbitrary master) rather than by
  slot. Worth a comment-linked assertion in the generator if this ever
  becomes false.
- **`HasUnknownKeys` commands (SORT's `BY`/`GET`/`STORE`, movable-keys
  commands generally) route using only whatever `KeySpecs` entries *are*
  statically resolvable**, silently ignoring the unresolvable ones rather
  than implementing Redis's server-side `getkeys`-equivalent logic
  client-side. For SORT specifically this means the primary source key still
  routes correctly; a `SORT ... STORE` writing to a genuinely different slot
  than the source key lives in would not be caught by the client-side
  CROSSSLOT pre-check (it would only surface via the server's own wire-level
  error, which the spec explicitly allows as the defensive fallback for
  exactly this kind of gap).
- ~~**The live-cluster fixture for `ClusterClientTests` is three manually
  Docker-launched `redis-server` processes**~~ Done — every integration
  fixture (standalone, TLS, Cluster, Sentinel) is now a disposable
  Testcontainers-managed one; see "Recorded during implementation of the
  Testcontainers conversion" near the end of this document.

### Recorded during §13 step 5 implementation (Sentinel support)

- **A real deadlock bug was found and fixed while writing this phase, not by
  review — by the test actually hanging.** `SentinelPubSubMonitor.Completion`
  only resolves once its *own* internal cancellation fires; the first draft
  of `PubSubSupervisorLoopAsync` did `await monitor.Completion` directly,
  with `await using var monitor = ...` disposing it only *after* that await
  returned. Since nothing ever cancelled the monitor's internal token from
  outside, and disposal — the only thing that would — was scoped to run
  after the await it was blocking, `SentinelClient.DisposeAsync` hung
  forever the moment the supervisor loop reached that line. A quick
  isolated console repro (connect, one command, dispose) didn't reproduce
  it — the race needed enough elapsed time for the supervisor loop to
  actually reach the blocking await first — but the real integration test,
  which does three round trips before disposing, hit it reliably. Fixed by
  racing `monitor.Completion` against a `Task.Delay(Timeout.Infinite,
  _lifetimeCts.Token)` instead of awaiting it directly, so `await using`'s
  disposal always runs promptly on shutdown regardless of which task
  "wins." Kept here because it's exactly the class of hazard §9's exhaustive-
  walker mandate exists to catch structurally rather than by luck — this one
  wasn't caught structurally, it was caught by a slow-enough test, which is
  the reminder to eventually give Sentinel's own state machines the same
  walker treatment as the blocking-cancellation race got in §2.6.
- **Master discovery quorum policy: strict majority of *responders*, not of
  all configured sentinels.** If 3 sentinels are configured but only 1
  answers within the per-sentinel timeout, that 1 response is accepted
  (1-of-1 is a majority of responders) rather than blocking forever waiting
  for a majority of the full configured set. This trades safety for
  availability in a way worth being explicit about: a partitioned client
  that can only reach one (possibly stale) sentinel will trust it. A future
  revision could require a minimum absolute responder count in addition to
  the majority-of-responders rule.
- **The known-sentinel list only grows, never shrinks.**
  `RefreshSentinelListAsync` (`SENTINEL sentinels`) adds newly-discovered
  peers but never prunes ones that stop responding or were removed from the
  constellation, mirroring the same simplification made for Cluster node
  health in §13 step 5's Cluster work. A sentinel that's genuinely gone
  just becomes one more endpoint that always fails quickly in the
  per-sentinel query fan-out.
- **No dedicated test exercises `RefreshSentinelListAsync`'s parsing path.**
  It's structurally identical to `ClusterTopology`'s Map/Array-shaped entry
  parsing (already tested there) and runs on every poll tick in practice,
  but the integration tests in this pass complete faster than one poll
  interval, so it's never actually exercised by them. Low-risk, but a real
  gap — worth a direct test once Sentinel's `PollInterval` is configurable
  (see the AWS IAM/Azure Entra credential-rotation precedent in §7 for why
  hardcoded intervals eventually want to become options).
- **`SentinelClient` and `ClusterClient` duplicate the same
  `ExecuteAsync`/`ExecuteBlockingAsync`/`BeginTransactionAsync` facade
  surface independently** rather than sharing an interface — project spec
  §10 explicitly calls for "a top-level `IRedisClient`... abstraction with
  the same shape for standalone, Cluster, and Sentinel-discovered
  connections," which doesn't exist yet. Both types wrap `RedisClient`
  instances and forward to them with the same three methods; unifying this
  is real, spec-mandated future work, not a new gap this phase introduced.
- **The Sentinel test fixture's config-persistence warnings are cosmetic,
  not functional.** The bind-mounted sentinel config files live in a
  directory the container user can't write a temp file into, so each
  sentinel logs "WARNING: Sentinel was not able to save the new
  configuration on disk" on every state change. Failover, quorum voting,
  and `+switch-master` all worked correctly regardless — sentinels operate
  fine in-memory — but a longer-lived version of this fixture would want the
  mounted directory itself writable, not just the file.

### Recorded during §13 step 6 implementation (managed/remote Redis)

- **Credential-rotation scheduling lives on `RedisConnection` itself, one
  self-rescheduling loop per connection**, rather than a shared
  pool-level scheduler. Simplest thing that works correctly per-connection;
  means N connections each independently call the provider on their own
  clock rather than in a single coordinated batch. Fine at Tier 1/Tier 2
  pool sizes (single digits to low tens of connections); would want
  revisiting if pool sizes grew enough that N independent provider calls
  every refresh interval became meaningfully wasteful (the provider is
  expected to cache/dedupe internally per its own contract, so this is a
  minor inefficiency, not a correctness issue).
- **Re-auth failure faults the connection rather than retrying the refresh
  itself.** This is deliberate, not a shortcut: it reuses the *existing*
  fault-then-replace machinery (Tier 1's health loop, Tier 2's lazy
  reconnect) to get "reconnect with new credentials" for free, exactly as
  designed — a fresh connection re-authenticates from the provider from
  scratch on its next connect. The tradeoff is that a single transient
  refresh hiccup (not a real credential problem) costs a full reconnect
  rather than a quick retry. Worth adding a bounded retry-before-fault if
  that tradeoff ever shows up in practice.
- **Tier 1's self-healing (`MultiplexedConnectionPool`'s background health
  loop) has no equivalent health-check for "connected but degraded"** — it
  only reacts to a connection actually reaching `Faulted`/`Closed`, not to
  e.g. rising latency or repeated command-level errors on an otherwise-Ready
  connection. Detecting "unhealthy but technically Ready" would need active
  probing (periodic `PING` with a latency budget), which is a reasonable
  next step but a distinctly bigger feature than reacting to a state the
  connection already tracks about itself.
- **`Rent()`'s all-slots-faulted fallback returns a connection it knows is
  dead** (so `SendAsync` throws immediately) **rather than waiting for a
  reconnect.** Chosen over blocking the caller: a fast, clear
  `RedisConnectionException` is easier for application-level retry logic to
  reason about than an unbounded wait with no cancellation path of its own
  (the caller's own `CancellationToken` still governs the `SendAsync` call,
  but there'd be nothing productive happening while waiting).
- **TLS was validated with server-only authentication (a custom certificate
  callback), not mutual TLS.** `RedisConnectionOptions.ClientCertificates`
  exists and is wired into `SslClientAuthenticationOptions`, but no test
  exercises a server that actually demands a client certificate — doing so
  needs a CA-signed client cert and a server configured with
  `tls-auth-clients yes`, which is more test-fixture machinery than this
  pass's TLS work needed to prove the core mechanism.
- **No AWS SigV4 or Azure Entra token implementation exists, deliberately**
  — `IRedisCredentialsProvider` is the seam the project spec asks for
  precisely so ReadUs never needs an AWS/Azure SDK dependency; the
  integration tests validate the seam itself (a rotating ACL password
  stands in for a rotating IAM/Entra token) rather than a specific cloud
  provider's signing scheme, which is out of scope for this client.
- **DNS re-resolution (§7's "don't cache a resolved IP for the lifetime of
  the client") was already satisfied by the existing design, not new work
  this phase** — every `RedisConnectionOptions.EndPoint` in use is a
  `DnsEndPoint`, and `Socket.ConnectAsync(EndPoint)` re-resolves it fresh on
  every call, including every reconnect through Tier 1's health loop or
  Tier 2's lazy reconnect. Recorded here since it's a real §7 requirement,
  even though nothing needed to change to satisfy it.

### Recorded during §13 step 7 implementation (convenience layer: DI, metrics, client-side caching)

- **DI registration blocks synchronously (`.GetAwaiter().GetResult()`)
  during singleton resolution** in
  `ReadUs.Extensions.DependencyInjection.ServiceCollectionExtensions`,
  because `RedisClient.ConnectAsync`/`ClusterClient.ConnectAsync`/
  `SentinelClient.ConnectAsync` are all inherently async (they connect to a
  real server) while `IServiceCollection.AddSingleton` has no async
  resolution path. Accepted as a documented tradeoff consistent with
  ecosystem norms (`ConnectionMultiplexer.Connect` does the same); an
  application that can't tolerate a blocking resolve should call the async
  `ConnectAsync` itself and register the resulting instance.
- **Metrics use only the BCL's `System.Diagnostics.Metrics`
  (`ReadUsDiagnostics`), with zero OpenTelemetry dependency in
  `ReadUs.Core`** — `ReadUs.Extensions.OpenTelemetry` exists purely to call
  `AddMeter(ReadUsDiagnostics.MeterName)` on an application's own
  `MeterProviderBuilder`; referencing it (and only it) is what actually
  costs anything.
- **`ObservableGauge<T>` was tried and abandoned for the "active
  connections" instrument.** The natural design — one gauge per
  `MultiplexedConnectionPool`, observed on demand — doesn't work in this
  runtime because `ObservableGauge<T>` doesn't implement `IDisposable`,
  which would leak every pool instance through the shared static `Meter`
  for the process's lifetime. Replaced with a single process-wide
  `UpDownCounter<int>` (`readus.connections.active`), driven imperatively
  from `RedisConnection.ConnectionOpened()`/`ConnectionFaulted(bool
  wasReady)`. Consequence: the metric is process-wide, not broken out
  per-pool-instance; revisit if per-pool attribution is ever needed (would
  require tagging the counter with a pool identity rather than switching
  instrument kind).
- **The shared static `Meter` is genuinely process-wide, which makes
  `MetricsTests.PoolReconnectIsRecordedWhenAFaultedSlotIsHealed` inherently
  observe other concurrently-running tests' pool healing, not just its
  own** — asserted as `>= 1`, not `== 1`, rather than trying to isolate the
  meter per test (not possible without changing `ReadUsDiagnostics` to be
  instance-scoped, which would then require threading a `Meter` instance
  through every pool/connection — a bigger change than this phase's metrics
  work justified).
- **`ClientSideCache`'s read-through/invalidation race — the substantive
  bug of this phase — took two attempts to close correctly, and the second
  attempt is the one that shipped.** The cache uses one dedicated
  `CLIENT TRACKING`-enabled connection (tracking is a connection property,
  not layerable over the pool); a read-through populates `_cache` after a
  `GET`, and an async RESP3 push (`>2\r\n$10\r\ninvalidate\r\n...`) evicts a
  key the moment any client writes it. The race: an invalidation for a key
  can arrive at any point between issuing that key's `GET` and populating
  the cache with its result.
  - *First attempt*: check-invalidated-and-clear-pending-marker *before*
    writing the cache. Left a gap — an invalidation arriving in the narrow
    window after the marker was cleared but before the write happened found
    the key in neither the pending-set nor the cache, did nothing, and the
    stale write then went uncorrected.
  - *Second attempt* (looked correct, wasn't): write the cache *first*,
    then clear the pending marker, then check an "invalidated while
    pending" flag — reasoning that the invalidation handler's own
    unconditional per-key `_cache.TryRemove` would always have *something*
    to act on. This missed that the invalidation handler's "remove from
    cache" and "is this key pending" were two independent
    `ConcurrentDictionary` calls, not one atomic step — and the read-through
    method's entire write-then-clear-pending sequence could run completely
    *between* those two calls: the cache-removal found nothing (write
    hadn't happened yet), and by the time the pending-check ran, the read
    had already cleared its own pending marker — so the invalidation was
    dropped with nothing left to catch it. This was caught, and the exact
    interleaving proven, with a 50-iteration concurrent-write stress test
    (`ClientSideCacheTests.NeverCachesAValueThatWasAlreadyInvalidatedWhileTheReadWasInFlight`)
    instrumented with a globally-sequenced diagnostic trace of each
    individual dictionary operation — the test failed even fully isolated
    (this was not a full-suite-contention artifact, despite initially
    looking like one).
  - *The fix that shipped*: a per-key lock, striped (`Environment
    .ProcessorCount * 4` stripes) so unrelated keys don't contend, making
    the read-through's post-`GET` completion and the invalidation handler's
    per-key handling atomic with respect to each other. Neither ever holds
    a stripe lock across the network round trip. `FLUSHALL`/null-payload
    invalidation acquires every stripe simultaneously (fixed acquisition
    order, so it can't deadlock against any single-stripe acquisition)
    before clearing the cache, making the flush atomic against every
    in-flight read across every key at once, the same way one stripe makes
    one key's invalidation atomic against that key's read.
- ~~**Only default (non-`BCAST`) tracking mode is implemented.**~~ `BCAST`
  and key-prefix tracking are now implemented — see "Recorded during
  implementation of BCAST tracking mode" below. Redirected tracking
  (`CLIENT TRACKING ... REDIRECT`) remains deliberately unimplemented: it
  exists for RESP2 clients, which have no way to receive an unsolicited
  invalidation on an ordinary command connection and so need a second,
  dedicated pub/sub connection to receive it on instead
  (`__redis__:invalidate`). This client always negotiates RESP3, which
  delivers invalidations as an ordinary out-of-band push on the same
  connection — there is no gap a ReadUs-specific redirect target would
  close.
- ~~**Higher-level typed helpers (POCO mapping) are deliberately out of
  scope for this pass.**~~ Done in a later pass — see "Recorded during
  implementation of typed JSON helpers" below (`ReadUs.Extensions.Json`).

### Recorded during implementation of §3.1 (Cluster node health tracking)

- **Fixed a latent bug in `ClusterClient.GetOrCreateNodeClientAsync`
  discovered while designing this feature, not caused by it**:
  `ConcurrentDictionary.GetOrAdd` caches whatever `Task<RedisClient>` its
  factory returns, fault included, and never re-invokes the factory for a
  key it already has an entry for. A node that failed its *first-ever*
  connection attempt was therefore permanently unreachable through a given
  `ClusterClient` instance, even after it recovered — every subsequent
  attempt just replayed the same cached exception. Fixed by checking
  `IsFaulted` and evicting via `ICollection<KeyValuePair<,>>.Remove`'s
  value-checked conditional removal (atomic against a concurrent
  replacement) before retrying. Required for this phase's own
  reintegration probe to mean anything — without it, a probe's retries
  would just re-observe the stale fault instead of attempting a new
  connection — but it's a real, independent correctness fix, not
  incidental to the health-tracking feature.
- **Chose a hard fail-fast circuit breaker over a half-open "let one trial
  call through" design.** Considered and rejected explicitly (see design
  doc §3.1's "not covered" note): a half-open state needs to coordinate a
  single trial attempt between whatever concurrent foreground callers show
  up and a background prober, which is real shared-state complexity for a
  scenario (a cluster master, which has no alternate routing target) where
  the background prober alone is sufficient and provably simpler to reason
  about.
- **Quarantine and reintegration are decided entirely by connection-level
  failures/successes, never by Redis-level error replies.**
  `MOVED`/`ASK`/`TRYAGAIN`/`CLUSTERDOWN`/an ordinary command error all mean
  the node responded — the opposite of what quarantine is tracking — so
  only a caught `RedisConnectionException` around the network round trip
  feeds the tracker, matching the same distinction the project draws
  everywhere else between transport failure and a RESP-level reply.
- **The consecutive-failure threshold (3) and probe backoff constants
  (200ms initial, 10s max, matching Tier 1's own reconnect loop exactly)
  are fixed, not configurable yet** — same status, and same reasoning, as
  `RedisConnection.UnblockReconciliationGrace`: simplest thing that let
  this be written and tested, revisit once cluster-specific configuration
  has a real home.
- **Verified against a real node failure, not a simulated one**: the
  integration test (`ClusterClientTests
  .QuarantinesARepeatedlyUnreachableNodeAndReintegratesItOnceItRecovers`)
  actually stops and restarts the `readus-cluster-7003` Docker container —
  the one test in that file that reaches for the container directly,
  since (unlike Sentinel's `SENTINEL FAILOVER`) there's no single Redis
  admin command that both makes a cluster node fully unreachable and later
  brings it back. Confirmed stable across repeated isolated runs and
  passing as part of the full integration suite; the cluster's own
  `cluster-require-full-coverage yes` setting means a longer outage would
  eventually cascade to `CLUSTERDOWN` cluster-wide, but the test's
  assertions all complete well inside Redis's own (much longer) node
  failure-detection timeout, so that cascade never enters into it.

### Recorded during implementation of §3.2 (Cluster replica read routing)

- **`RedisConnectionOptions` changed from a `class` to a `record`**,
  specifically so this feature could layer a `READONLY`
  `PostConnectAsync` hook onto whatever options a caller's own factory
  returns via a `with` expression, rather than hand-copying every
  property into a new instance (a helper that would silently go stale
  the next time a property is added). Confirmed via grep before making
  the change that nothing in the codebase relies on this type's
  reference equality; the change is additive only (value equality,
  `with` support, a generated `ToString()`) and required no other call
  site to change.
- **`RedisConnectionOptions.PostConnectAsync` is a new, general-purpose
  hook in `ReadUs.Core`, not a Cluster-specific concept** — it runs once
  per physical connection, right after `RedisConnection.ConnectAsync`
  reaches `Ready`, for *every* connection made with those options,
  including a pool's self-healing reconnect replacement. This is what
  lets `READONLY` "manage this transparently as part of the
  dedicated-vs-pooled connection lifecycle, don't leak it into the
  public API" (the spec's own words) without `ReadUs.Core` needing to
  know anything about Cluster or replicas — Cluster is just the first
  caller of a seam that any other layer could use later.
- **The permanent local test fixture grew a fourth node** (an actual
  `docker run`, not a stop/start toggle like the quarantine test):
  `readus-cluster-7004`, joined to the existing 3-master cluster via
  `CLUSTER MEET` and made a real replica of node 1 (port 7001, slots
  0-5460) via `CLUSTER REPLICATE`. Necessary because the fixture had been
  "3-master, no-replica" since the original Cluster phase — there was no
  way to exercise the replica happy path (an actual read served *from* a
  replica) against real infrastructure without one. `ClusterClientTests`'
  class-level doc comment now describes this; nodes 2 and 3 still have no
  replica, which is exactly what
  `ReplicaOnlyThrowsWhenTheTargetShardHasNoReplica` needs.
- **Proving `READONLY` was actually issued (not just "the read didn't
  throw") needed more than a happy-path assertion**: a replica that never
  received `READONLY` still answers a read — with `MOVED`, which this
  client's own redirect-following logic transparently retries against
  the primary, silently masking a missing hook as a passing test.
  `PreferReplicaRoutesToTheReplicaWithoutFollowingAMovedRedirect` instead
  asserts on the `readus.cluster.redirects{kind=moved}` metric (already
  built for the quarantine phase's diagnostics) being exactly zero for
  that call — a real, mechanical proof rather than an assumption.
- **A real, non-obvious test-authoring pitfall hit while writing this
  phase's tests**: two new tests initially reused the existing
  `FindKeyInSlotRange` helper against the *same* slot range (0-5460) that
  `FollowsARealMovedRedirectAfterALiveSlotMigration` already depends on
  for a deterministic, "guaranteed never used" key — but that helper is
  deterministic *by design* (same range in, same key out), so the new
  tests silently wrote a value into the exact key the older test assumes
  is always empty. The older test failed consistently afterward, in
  isolation, with no code changes anywhere near it — worth remembering
  next time a "why did an unrelated test start failing" investigation
  starts, since the instinct to suspect the new code first would have
  been wrong here. Fixed by adding `FindRandomKeyInSlotRange` (GUID-based,
  no determinism, no collision risk) for tests that set their own value
  and don't need the "never used" guarantee; the stray value already
  written into the shared cluster during debugging was deleted by hand
  once identified.
- **Also closed, while touching this exact code**: the `GetOrAdd`-can-race
  gap the original §13 step 5 "Recorded" list already named and already
  suggested the fix for (`Lazy<Task<T>>`). Replica routing added a second
  caller of the same "get-or-connect" pattern (`GetOrCreateReplicaClientAsync`,
  alongside the existing `GetOrCreateNodeClientAsync`), which was reason
  enough to fix it properly rather than duplicate the same latent race into
  a second call site. `_nodeClients` is now
  `ConcurrentDictionary<string, Lazy<Task<RedisClient>>>`; both callers
  share one `GetOrCreateClientAsync(EndPoint, Func<EndPoint,
  RedisConnectionOptions>)` helper parameterized by which options factory
  to use (plain vs. the `READONLY`-wrapped one). `Lazy<T>`'s
  `ExecutionAndPublication` mode guarantees at most one caller ever
  actually evaluates the factory and opens a connection, regardless of how
  many concurrently race `GetOrAdd` for the same not-yet-contacted node —
  eliminating the discard-and-leak entirely rather than just making it
  less likely. Verified with a real concurrent-load integration test
  (`ConcurrentFirstContactWithANewNodeOnlyEverConnectsOnce`): a custom
  options factory counts its own invocations per endpoint, and 30
  concurrent commands racing first contact with a node still show exactly
  one connection attempt.

### Recorded during implementation of typed JSON helpers

- **Scoped to whole-value `GET`/`SET` only, deliberately, not hash-field
  mapping.** Mapping a POCO's properties onto `HSET`/`HGETALL` is a
  distinctly bigger design task (partial-update semantics, `HGETALL`'s
  flat field/value reply shape, what a missing field on read means) —
  left for a follow-up if whole-value JSON mapping proves worthwhile.
  `SetJsonAsync`/`GetJsonAsync` just serialize/deserialize the entire
  value as one JSON blob through ordinary `SET`/`GET`.
- **A new package, `ReadUs.Extensions.Json`, not a `ReadUs.Core` addition**
  — project spec §8 is explicit that reflection-driven convenience
  "belongs in an optional, clearly-labeled higher-level convenience
  package, never in the core command path," matching the same pattern
  `ReadUs.Extensions.DependencyInjection`/`.OpenTelemetry` already
  established: referencing the package (and only it) is what costs
  anything.
- ~~**Every operation ships two overloads: a `JsonTypeInfo<T>` one ... and a
  `JsonSerializerOptions` one (ordinary `System.Text.Json` runtime
  reflection, for callers who haven't set one up).**~~ Reversed: the
  `JsonSerializerOptions` overload was removed once "zero reflection
  anywhere in this client's surface, including the optional convenience
  packages" became the actual bar the user set, not just the core command
  path §8 mandates it for. `JsonTypeInfo<T>` — a caller's own
  source-generated `JsonSerializerContext` — is now the only path. A
  pragmatic-middle-ground argument for keeping the reflection fallback
  (matching how ASP.NET Core minimal APIs resolves the same tension) was
  the original reasoning; it didn't survive contact with what the user
  actually wanted for this client specifically.
- **A real (if minor) language limitation surfaced while writing this**:
  `ReadOnlySpan<byte>` can't be a generic type argument (ref structs never
  can), so a first attempt at sharing the null/error-checking logic
  between the `JsonTypeInfo<T>` and `JsonSerializerOptions` `GetJsonAsync`
  overloads via a `Func<ReadOnlySpan<byte>, T?>` helper didn't compile.
  Fixed by extracting a non-generic `TryGetJsonPayload(RedisResult reply,
  out ReadOnlySpan<byte> span)` instead — `out`/`ref` ReadOnlySpan
  parameters are fine, it's specifically the generic-argument position
  that's disallowed.

### Recorded during implementation of batch execution

- **The honest scope of this feature turned out smaller than the original
  §13 step 5 note implied.** That note framed the missing piece as "a
  batch API that fans a single logical batch out across multiple nodes by
  destination and reassembles replies in the caller's original order," as
  if grouping-by-destination were a mechanism still to be built. It isn't:
  `ClusterClient.ExecuteAsync` already routes each command by its own slot
  through the same redirect-handling path regardless of caller, and Tier
  1's FIFO completion-claim protocol (design doc §2.4) already pipelines
  correctly whenever several calls are concurrently in flight on the same
  connection. Firing N commands without awaiting any of them first, *then*
  joining them, already produces correct per-node fan-out and pipelining
  as an emergent property of machinery that was already built and already
  tested — there was no separate grouping step to design. `ExecuteBatchAsync`
  (`RedisClient`, `ClusterClient`, and a one-line delegating overload on
  `SentinelClient` for API parity) is real, and its ordered-array,
  single-call ergonomics are the genuine value it adds — but it does not
  contain new dispatch logic, and the design doc should say so plainly
  rather than imply otherwise.
- **Implemented with `Task.WhenAll` over `.AsTask()`-converted calls, not
  by storing the `ValueTask<RedisResult>`s an array for later awaiting.**
  The latter is what a first draft did, and while every item actually was
  consumed exactly once regardless, `CA2012` correctly flags storing a
  `ValueTask` in a local as looking like a bug (it usually is) and the
  project has zero tolerance for new warnings. `.AsTask()` immediately
  converts each to an ordinary `Task<RedisResult>` (no single-consumption
  constraint, forces a real allocation — acceptable here since a batch API
  is already allocating an array per call, not a steady-state hot path),
  and `Task.WhenAll` is the idiomatic, well-understood primitive for
  exactly this "fire N, join N" shape.
- **A connection-level failure on any one item aborts the whole batch and
  propagates — `Task.WhenAll`'s own behavior, not a custom partial-failure
  model.** An ordinary command-level error (a RESP error reply) never
  does; it's captured per-item via `RedisResult.IsError`, same as a single
  `ExecuteAsync` call. Considered returning `Task<RedisResult>[]` instead
  (never losing partial results to one item's connection failure) but
  rejected it: no other part of this client's API models partial failure
  this way (`RedisTransaction`'s `EXEC` is all-or-nothing at the protocol
  level too), and `Task.WhenAll`'s ordinary semantics are what every other
  caller of this pattern in .NET already expects.
- **No per-item read-preference override in a Cluster batch** — every
  item uses `ReadPreference.PrimaryOnly`. A caller wanting replica reads
  for some batch items and not others can still get there today (call the
  single-command `ReadPreference` overload directly instead of batching
  those specific items), so this is a real but low-cost scope limit, not a
  capability gap.

### Recorded during implementation of the Testcontainers conversion

Project spec §9.2 asks for tests "spun up via Testcontainers or an
equivalent throwaway-process harness, not mocked." Every integration
fixture had instead been a long-lived, manually `docker run` container —
started once early in the session and left running so tests stayed
runnable across later sessions, growing ad hoc as later phases needed
more (a 4th cluster node added for replica routing, an ACL-capable
standalone server, a TLS-enabled instance, a full Sentinel constellation).
Converted all four (`StandaloneRedisFixture`, `TlsRedisFixture`,
`ClusterRedisFixture`, `SentinelRedisFixture`) to disposable,
per-test-run containers via `Testcontainers`/`Testcontainers.Redis`,
shared within a run via xUnit `ICollectionFixture`.

- **Host networking for Cluster and Sentinel, ordinary bridge networking
  for standalone and TLS** — the deciding factor is whether the server
  announces its own address to anything. Cluster nodes gossip their
  announced address to each other and report it via `CLUSTER SHARDS` to
  any client that asks; Sentinel announces the master's address and its
  own to peer sentinels. Every one of those addresses has to be something
  both the other containers *and* this out-of-Docker .NET test process
  can reach — bridge networking's per-container random-port-mapping model
  has no way to satisfy that without the containers announcing an address
  the host-side process can't route to. Host networking sidesteps the
  whole problem: a container's ports *are* the host's ports, so a free
  host port found (`FreePort`, a `TcpListener` bound to port 0) before a
  container starts is unambiguously the one address everyone uses. A
  standalone server or a TLS-terminating one never announces anything to
  a peer, so ordinary bridge networking with Testcontainers' own random
  host-port mapping is simpler and was kept for those two.
- **Two real, non-obvious infrastructure problems surfaced while building
  the Cluster and Sentinel fixtures, neither one a ReadUs bug** — both
  worth remembering for any future fixture work in this vein:
  - `ConcurrentDictionary.GetOrAdd`-shaped test-authoring pitfall aside
    (see the §3.2 entry above), the Testcontainers-specific one was a
    permissions problem: `redis-sentinel` rewrites its own config file to
    persist discovered state, so the file has to actually be writable by
    the process running it. Copying it in via `WithResourceMapping`, even
    with fully permissive mode bits, still left it unwritable — almost
    certainly an ownership mismatch against whichever user the redis
    image's entrypoint drops privileges to before exec'ing
    `redis-sentinel`. Fixed by writing the config via a root shell command
    at container startup instead (`sh -c "echo <base64> | base64 -d >
    /etc/sentinel.conf && exec redis-sentinel ..."`), overriding the
    entrypoint entirely — the image's own `redis-server`/`redis-sentinel`
    argument-sniffing (the thing that triggers the privilege drop) never
    runs, so the whole chain, sentinel process included, just stays root.
    Fine for a throwaway test container; would not be an acceptable fix
    for anything the image was actually deployed as.
  - Testcontainers' generic `ContainerBuilder` has no first-class "host
    networking" method — `WithNetworkMode` doesn't exist on it. The
    supported escape hatch is `WithCreateParameterModifier`, which hands
    back the raw Docker.DotNet `CreateContainerParameters` for exactly
    this kind of thing not covered by the fluent API
    (`parameters.HostConfig!.NetworkMode = "host"`).
- **Real, measured cost: per-run bootstrap time.** The full integration
  suite went from ~3-9s (against already-running manual containers) to
  ~10-17s (standing up a standalone server, a TLS server, a 4-node
  Cluster with a live `redis-cli --cluster create` bootstrap, and a full
  Sentinel constellation, all from scratch, every run). Confirmed stable
  across many repeated full-suite runs during this conversion. Judged
  worth it for the reproducibility spec §9.2 asks for — no test's
  correctness depends anymore on a container someone remembered to start
  in a previous session, or on a fixed port happening to be free.

### Recorded during implementation of Redis hash mapping (`ReadUs.Extensions.Hashes`)

Direct follow-up to the typed JSON helpers: once the user asked "would it
be possible to use source generation... to get zero reflection," two
things happened — the JSON helpers' `JsonSerializerOptions` (reflection)
overload was removed outright (zero reflection is now the actual bar for
this client, not just the core command path per project spec §8), and
this package was built as the "real Hash mapping" item that was already
on the deferred list. Unlike the JSON helpers (which lean on
`System.Text.Json`'s own source generator), this one is ReadUs's own: a
second `IIncrementalGenerator` (`HashModelGenerator`) added to the
existing `ReadUs.SourceGenerators` project, alongside
`CommandTableGenerator`.

- **A genuinely different kind of generator from the existing one, and
  that shaped several decisions.** `CommandTableGenerator` reads a
  vendored JSON snapshot of *Redis's own* commands and emits code once,
  baked into `ReadUs.Core.dll` at ReadUs's own build time — a consuming
  application never needs the generator to run again. `HashModelGenerator`
  reads *the consuming application's own* POCOs, decorated with
  `[RedisHashModel]`, and must run inside *that application's* build
  every time — it uses `ForAttributeWithMetadataName` (the standard
  efficient incremental-generator pattern for "find types with this
  attribute," re-running only for changed syntax, not the whole
  compilation) rather than `AdditionalTextsProvider`. Both the marker
  attribute (`RedisHashModelAttribute`) and the `IRedisHashModel<TSelf>`
  interface the generated code implements live as ordinary types in the
  runtime package (`ReadUs.Extensions.Hashes`), not emitted via
  `RegisterPostInitializationOutput` — simpler, and the interface in
  particular is a stable contract better versioned as a normal library
  type than regenerated text.
- **Zero reflection is achieved architecturally, not just by omission**:
  every property access in the generated `ToHashFields`/`FromHash`
  methods is an ordinary compile-time-resolved member access — `this.Name`,
  `new Person(...)` — exactly as if a person had hand-written it. The
  generic constraint making this possible, `IRedisHashModel<TSelf>` with
  a C# 11 `static abstract FromHash(...)` member, lets
  `HashRedisClientExtensions.GetHashAsync<T>` call `T.FromHash(reply)`
  with the compiler resolving the correct implementation at the call
  site — no `Activator.CreateInstance`, no `PropertyInfo`, nothing
  reflection-based anywhere in the path.
- **Only two constructor shapes are supported: a public parameterless
  constructor (plain mutable POCO, mapped via object initializer) or a
  public constructor whose parameters exactly match every mapped property
  by name (a positional `record`, purely positional construction).**
  Anything else — a record with *some* primary-constructor properties and
  *some* separate mutable ones, multiple constructors that each partially
  match — is rejected with a clear compile-time diagnostic
  (`READUSHASH004`) rather than guessed at. These two shapes cover the
  overwhelming majority of real POCOs; the alternative (a general
  best-fit constructor solver) is real complexity for shapes that are
  rare and arguably ill-advised for a hash-mapped model anyway.
- **A missing field throws on read unless the property is nullable** —
  deliberately, not silently defaulting to `0`/`false`/etc. A property
  typed `int?`/`string?`/etc. (or the value never written in the first
  place, e.g. from an older version of the model) comes back `null`;
  everything else throws a clear `InvalidOperationException` naming the
  field and type. Symmetric on write: a null-valued nullable property is
  skipped entirely rather than writing an empty-string sentinel — Redis
  hashes are naturally sparse, so "field absent" is already the correct
  representation of "no value," with no separate encoding needed.
- **Scoped to scalars only, deliberately** — `string`, `byte[]`, `int`,
  `long`, `double`, `bool`, `Guid`, `DateTime`, any `enum`, and a nullable
  of any value-typed one of these. No nested objects, no collections
  (other than the `byte[]` scalar itself), no dictionaries — an
  unsupported property type is a compile-time diagnostic
  (`READUSHASH003`), not a silent skip or a runtime surprise. Matches how
  the original command generator caps its own nesting depth (design doc
  §13 step 4 "Recorded" entry): support the shapes that cover the
  overwhelming majority of real use, fail loudly and immediately on
  anything past that line rather than half-supporting it.
- **A real, non-obvious C# scoping bug caught immediately by the first
  test with more than one nullable property**: the emitter's first draft
  used a fixed pattern-variable name (`if (this.X is T __v)`) for every
  nullable property. A pattern variable introduced in an `if` condition
  stays in scope for the *rest of the enclosing block*, not just that
  one `if` statement's body — so a *second* nullable property's `if (...
  is T __v)` collided with the first as "already defined in this scope."
  Fixed by giving each property's pattern variable a unique name
  (`__v{PropertyName}`). Caught at compile time (the generated code
  simply didn't compile), which is exactly the point of the "generate
  code, let the C# compiler be the correctness check" approach — a
  runtime-reflection-based mapper would have had no equivalent safety net
  for an analogous bug.
- **Test coverage at both levels, matching how `CommandTableParser` is
  tested**: `HashModelParserTests` (`ReadUs.Tests.Unit`) compiles small
  source snippets in-process (via `CSharpCompilation`/a semantic model)
  and asserts on `HashModelParser`'s classification and diagnostics
  directly, without needing the generator pipeline or a live server;
  `HashRedisClientExtensionsTests` (`ReadUs.Tests.Integration`) proves the
  generator's *output* actually compiles and round-trips correctly
  against a real, disposable Testcontainers-managed server, including the
  missing-required-field throw and the null-nullable-fields-omitted paths.

### Recorded during implementation of BCAST tracking mode

- **The core race-closing machinery (the striped per-key locks,
  `GetAsync`'s write-then-check-pending sequence,
  `HandleInvalidation`'s atomic-per-key handling) needed zero changes.**
  The insight that made this a small, contained addition rather than a
  redesign: BCAST changes *which* keys can generate an invalidation for
  this connection (any write under a registered prefix, not just keys
  this connection has personally read) but not the *shape* of the race —
  an invalidation for a key currently mid-read is exactly the same hazard
  either way, and the wire format of the invalidation push itself
  (`>2\r\n$10\r\ninvalidate\r\n...`) is identical in both modes,
  including the null-payload "flush everything" case. Only the `CLIENT
  TRACKING` setup command needed to become configurable
  (`TrackingMode.BuildClientTrackingArgs`) — a small, additive `TrackingMode`
  struct (`Default` / `Bcast(params string[] prefixes)`), not a
  redesign of anything already built.
- **A new client-side pre-flight check, in the same spirit as CROSSSLOT
  and the Cluster read-preference validation**: in BCAST mode with one or
  more explicit prefixes, `GetAsync` rejects a key that doesn't start
  with any of them, with `ArgumentException`, before touching the
  network. Reasoning: the server will never send an invalidation for that
  key to this connection (it's outside every prefix this connection
  subscribed to), so caching it anyway would create a value this cache
  can never learn is stale — a real correctness hazard, not just a
  usage nicety, so it's caught the same way the project already catches
  CROSSSLOT and an invalid `ReadPreference`/command pairing: client-side,
  before anything is sent, rather than left as a silent trap.
- **BCAST with zero prefixes means "every key in the keyspace" (Redis's
  own documented behavior), and `TrackingMode.CoversKey` treats it
  identically to `Default` mode for the pre-flight check** — no
  restriction, since there's no prefix to violate. Verified directly
  (`BcastModeWithNoPrefixesCoversAnyKey`).
- **No new way was added to verify from the *client* side that Redis
  actually accepted and applied BCAST mode** (e.g. via `CLIENT
  TRACKINGINFO`, which only reports on the calling connection's own
  state and so isn't queryable from a separate admin connection).
  Considered and deliberately not pursued for this pass: `ConnectAsync`
  already throws `RedisConnectionException` if the server rejects the
  `CLIENT TRACKING ON BCAST PREFIX ...` syntax, and the integration tests
  prove the *behavior* end-to-end (reads, writes, and invalidations all
  working correctly for a key within a registered prefix) — between
  those two, there's no gap in confidence that would justify adding
  `ClientSideCache` surface area purely for introspection.

### Recorded during implementation of fault-injection tests (project spec §9.2)

Of §9.2's fault-injection list, everything except two items already had
real-infrastructure coverage: MOVED/ASK/CLUSTERDOWN sequences
(`ClusterClientTests`, against a live migration/redirect, not a
simulation), a forced Sentinel failover (`SentinelClientTests`, a real
`SENTINEL FAILOVER`), and blocking-command cancellation racing the
server's reply (the exhaustive completion-claim state-space walker, §2.6,
plus `BlockingCommandTests`). The two genuinely missing: mid-response TCP
resets, and partial writes/reads. `FaultInjectionTests` covers both,
using Toxiproxy — the spec's own named option ("via a proxy layer like
Toxiproxy or a custom byte-delaying stream wrapper") — rather than a
hand-rolled proxy, on the reasoning that a purpose-built, widely-used
fault-injection tool is less likely to have its own subtle bugs than a
first attempt at reinventing one.

- **`ToxiproxyFixture` sits Toxiproxy and a target Redis on a private
  Docker network together**, with the .NET test process only ever
  talking to Toxiproxy's own mapped ports — never directly to Redis. This
  is what makes an injected fault genuinely sit on the wire between
  ReadUs and the server, not just a mock standing in for one.
- **A real, non-obvious Testcontainers gotcha, not a ReadUs bug**: the
  official Toxiproxy image is scratch-based with no shell at all — no
  `sh`, no `cat`, nothing. An exec-based wait strategy
  (`UntilInternalTcpPortIsAvailable`, used successfully for every other
  fixture's plain Linux-distro-based Redis image) can never succeed
  against it, and — this is the sharp edge — it doesn't fail loudly when
  it can't; it just retries forever, hanging the whole test run with no
  error. Diagnosed by adding a file-based diagnostic log at each fixture
  step (Console output isn't reliably captured by the test runner,
  same lesson as the earlier `ClientSideCache` race investigation) and
  observing exactly where progress stopped. Fixed with an HTTP-based wait
  strategy (`UntilHttpRequestIsSucceeded` against Toxiproxy's own
  `/version` endpoint) — performed from outside the container, needing
  nothing inside it at all. Worth remembering for any future
  minimal-image fixture: prefer an HTTP/TCP-level wait check over an
  exec-based one unless the image is known to have a shell.
- **One shared Toxiproxy proxy for the whole fixture, not one per test**:
  a proxy's listen port is a container port that has to be mapped
  *before* the container starts, which rules out creating a fresh proxy
  on a fresh port per test without restarting the container. Tests add
  and remove their own named toxics against the one shared proxy instead
  — safe because collection tests run sequentially, so there's no
  cross-test toxic interference.
- **Once the fixture's wait-strategy bug was fixed, both fault scenarios
  behaved exactly as hoped, and fast**: the mid-response reset produces
  `RedisConnectionException` in milliseconds (no hang, no ambiguous
  failure mode — this is genuinely reassuring evidence for "provably
  stable," not just an assumption), and 500 bytes of reply data sliced
  into 1-byte network fragments with a delay between each still parses
  correctly, proving the zero-copy `ReadOnlySequence<byte>` reassembly
  path (design doc, project spec §8) holds up against real socket-level
  fragmentation, not just an in-memory unit test's synthetic split.
- **A real, if minor, side effect of adding a sixth concurrent fixture
  collection**: `ClientSideCacheTests`' eviction-polling tests (50 × 20ms
  = 1s budget) started flaking under the added concurrent Docker load
  from `ToxiproxyFixture`'s extra containers starting up alongside
  everything else. Widened to 300 × 20ms = 6s, matching the more generous
  budget this file's own race test already uses for the identical
  reason (see its own remarks) — confirmed stable across four repeated
  full-suite runs afterward.

### Recorded during implementation of Pub/Sub

Project spec §10 named a general-purpose Pub/Sub API
(`SUBSCRIBE`/`PSUBSCRIBE`/cluster-sharded `SSUBSCRIBE`, "an
`IAsyncEnumerable<RedisMessage>`-style subscription API") as part of the API
shape from the start — unlike every other scoped-out item in this §5, it was
never recorded here as a deliberate deferral. It was simply missed until a
later audit (prompted by a smaller, separate ask to unify
`RedisClient`/`ClusterClient`/`SentinelClient` behind a shared `IRedisClient`
— built later, see "Recorded during implementation of IRedisClient
unification" near the end of this document) went looking for what else §10
asked for and never got.

- **A real RESP3 protocol wrinkle is the whole reason this needed a
  dedicated type rather than just calling the command-table generator's
  already-emitted `PubsubCommands.SubscribeAsync`/etc. extension methods.**
  Since ReadUs always negotiates RESP3 (project spec §1), the server sends a
  `SUBSCRIBE`/`PSUBSCRIBE`/`SSUBSCRIBE` confirmation (and their
  `UN`/`PUN`/`SUN` counterparts) as an out-of-band Push frame, not as the
  ordinary correlated reply `RedisConnection.SendAsync` expects. The
  generated `SubscribeAsync` extension method calls the ordinary
  `ExecuteAsync` path and would hang forever if actually used to subscribe —
  `DispatchReply` hands a Push frame to `OnPush` and returns without ever
  dequeuing the matching `PendingRequest`. Left in place regardless (it's
  still a technically-correct low-level escape hatch for hand-rolled push
  handling) rather than removed, but noting this plainly so it isn't
  mistaken for a working way to subscribe, or for dead code, later.
- **`RedisConnection` grew two small additions to make `RedisSubscriber`
  possible without weakening any existing invariant**: `SendSubscriptionCommandAsync`
  (internal) writes a command under the same `_writeGate` as ordinary
  traffic but enqueues no `PendingRequest`, and `OnFaulted` (public, mirrors
  the existing minimal `OnPush` seam) fires once when the connection faults,
  so a consumer with its own state tied to the connection's lifetime (here,
  every open subscription's `Channel<T>`) learns about the fault immediately
  rather than discovering it lazily — or never, if nothing else would ever
  call back in. Without `OnFaulted`, a `RedisSubscriber` whose dedicated
  connection died would leave any open `await foreach` hanging forever, with
  nothing left to ever write to its channel again — exactly the H1/H2 "never
  leave a caller waiting forever" invariant this project already enforces
  for blocking-command cancellation.
- **Deterministic unsubscribe via `try`/`finally` around the async
  iterator's `yield return` loop**, not an explicit `UnsubscribeAsync`
  method a caller has to remember to call. Breaking out of an `await
  foreach`, cancelling it, or disposing the enumerator early all run the
  `finally` block, which sends the matching `UNSUBSCRIBE`/`PUNSUBSCRIBE`/
  `SUNSUBSCRIBE` with `CancellationToken.None` — deliberately not the
  caller's own (possibly already-cancelled) token, since the caller
  cancelling enumeration is often *why* the cleanup is running in the first
  place, and the unsubscribe should still be attempted regardless. Matches
  the "owns its lifetime, cleans up on scope exit" shape `RedisTransaction`
  and `ConnectionLease` already use.
- **Cluster sharded pub/sub needed one real piece of new routing, not just
  a pass-through.** Ordinary `PUBLISH` is cluster-bus-propagated (delivered
  cluster-wide regardless of which node a subscriber connects to), but
  `SSUBSCRIBE`/`SPUBLISH` delivery is shard-local — a message only reaches
  subscribers connected to a node owning the shard channel's hash slot.
  `ClusterClient.CreateShardSubscriberAsync` computes the slot via the
  existing `HashSlot.Compute`, resolves the owning node via the client's own
  `_topology.FindOwner`, and opens a `RedisSubscriber` directly against it —
  reusing existing routing knowledge rather than duplicating slot math.
  Verified end-to-end against a real Cluster fixture (not just "SSUBSCRIBE
  doesn't error"): `SPUBLISH` already carries a proper key spec
  (`FirstKeyPosition = 1`) in the vendored command table, so
  `ClusterClient.ExecuteAsync`'s existing per-key routing sends it to the
  same node the subscriber resolved to, and the test's fast completion time
  (a couple hundred milliseconds, not a redirect-chase's multiple round
  trips) is itself evidence the routing landed correctly on the first try.
- **Two documented non-goals, matching existing precedent rather than
  silently under-building**: no auto-reconnect after a fault (identical
  scope limit to `ClientSideCache`) — a caller must open a fresh
  `RedisSubscriber` and re-subscribe; and `SentinelClient.CreateSubscriberAsync`
  opens against the *current* master and does not follow a later failover —
  unlike `SentinelClient.ExecuteAsync`, which always reads the live
  `_masterClient` reference at call time, a subscription is one dedicated
  physical connection handed to the caller up front, with no way for a
  later `SwitchMasterAsync` to migrate it. A caller needing failover
  resilience detects the fault (the subscription's `await foreach` ends
  with an exception) and calls `CreateSubscriberAsync` again.
- **The audit that found this gap in the first place also confirmed
  `IRedisClient` (spec §10's "same shape for standalone, Cluster, and
  Sentinel-discovered connections") didn't exist yet at this point** —
  `RedisClient`/`ClusterClient`/`SentinelClient` each independently
  duplicated the same facade surface, as already noted in this document's
  §13 step 5 Sentinel section. Deferred again here, this time in favor of
  Pub/Sub and Scripting, at the user's explicit direction — `RedisScript`'s
  per-client adapters (see "Recorded during implementation of Scripting"
  below) turned out to be a second independent motivation for building it,
  and it was in fact built immediately afterward; see "Recorded during
  implementation of IRedisClient unification" near the end of this
  document.

### Recorded during implementation of Scripting

The other half of the same missed-not-deferred spec §10 item as Pub/Sub:
`EVAL`/`EVALSHA`/`FUNCTION` support "with automatic script-cache-miss
fallback" and "a typed wrapper so a script's declared keys/args are checked
rather than passed as loose object arrays."

- **No protocol wrinkle here, unlike Pub/Sub** — `EVAL`/`EVALSHA`/
  `FCALL`/`FUNCTION LOAD` etc. already have working generated typed methods
  on `RedisClient` (`ReadUs.Generated.ScriptingCommands`), ordinary
  request/reply commands with no RESP3 push involved. `RedisScript`'s real
  value is purely the ergonomic layer spec §10 actually asked for: a
  self-computed SHA1 (`SHA1.HashData` over the script's UTF-8 bytes,
  `Convert.ToHexStringLower` — no round trip via `SCRIPT LOAD` needed to
  learn it) and automatic `EVALSHA`→`NOSCRIPT`→`EVAL` fallback.
- **Deliberately no client-side "is this script loaded" cache.** The
  server's own script cache can be evicted independently of this client
  (`SCRIPT FLUSH`, a restart), so a client-side "definitely loaded"
  assumption could go stale in a way that would silently break every call
  after that point. Optimistic-`EVALSHA` + catch-`NOSCRIPT` is the only
  version of this that can't go stale — matches how StackExchange.Redis's
  own `LuaScript` handles the identical tension. Verified for real, not just
  argued: `EvaluatingAgainAfterAServerSideScriptFlushStillSucceeds`
  (`ScriptingTests`) issues a genuine `SCRIPT FLUSH` against the live server
  between two calls of the same `RedisScript` and confirms the second call
  still succeeds via the fallback.
- **`RedisCommandExecutor`, a delegate, not a new shared interface**, is
  what lets one `RedisScript.EvaluateAsync` work against all three client
  types via three one-line adapter extension methods
  (`RedisScriptClientExtensions`/`ClusterScriptingExtensions`/
  `SentinelScriptingExtensions`) — deliberately not reaching for the
  still-deferred `IRedisClient` unification to solve this narrower problem;
  see the Pub/Sub section above for why that's a separate, not-yet-built
  piece of work. `RedisClient`/`ClusterClient`/`SentinelClient` all already
  expose an `ExecuteAsync` matching the delegate's shape exactly, so the
  adapters are pure method-group conversions.
- **`FUNCTION`/`FCALL` deliberately got no equivalent wrapper.** Unlike a
  script, a function is explicitly loaded once (already-generated
  `FunctionLoadAsync`) and then called by name (already-generated
  `FcallAsync`/`FcallRoAsync`) — there's no cache-miss/fallback wrinkle at
  all to automate, so a bespoke wrapper would just be a no-op pass-through
  restating what the generated surface already does. Not built, matching
  spec §8's "don't add complexity speculatively" stance, cited for the same
  kind of call elsewhere in this document.
- **Cluster routing is correct but not optimal for a keyed script call, and
  that's an accepted, pre-existing pattern, not new scope.** `EVAL`/
  `EVALSHA`'s keys are staticaly resolvable in principle (`numkeys` plus a
  fixed-position key list), but the command-table generator currently marks
  them `HasUnknownKeys = true` — confirmed directly in the generated
  `CommandMetadata.g.cs` (`KeySpecs = new GeneratedKeySpec[] { }` for both).
  A Cluster script call therefore round-robins to an arbitrary master on
  the first attempt rather than routing by key, exactly the same situation
  already accepted and documented for `SORT` (§13 step 5's Cluster
  section): a wrong first guess surfaces as an ordinary `MOVED` reply,
  which `ClusterClient`'s existing redirect-following already handles
  transparently. `ClusterScriptingTests.EvaluatingAScriptWithAKeySucceedsRegardlessOfWhichNodeItFirstRoutesTo`
  proves the whole round trip still succeeds end-to-end against a real
  Cluster fixture, redirect included — correct, just not routed optimally
  on the first try. Teaching the generator `EVAL`'s `numkeys`-relative key
  spec shape (rather than the simple first/last/step form it supports
  today) would close this properly; not attempted here, since it's a
  generator change orthogonal to this phase's actual scope.
- **A hand-recalled SHA1 test vector turned out to be wrong when actually
  checked** — worth remembering as a small, concrete instance of the
  project's own "verify, don't trust recall" discipline (already invoked
  for the Toxiproxy JSON shapes during the fault-injection phase).
  `RedisScriptTests.Sha1MatchesTheStandardTestVector` originally asserted
  against a from-memory value for SHA1("abc") that was one trailing hex
  digit short; cross-checking independently via both `sha1sum` and Python's
  `hashlib` (not by re-deriving it from this same implementation) caught it
  immediately. Fixed to the tool-confirmed value before the test was ever
  reported as passing.

### Recorded during implementation of IRedisClient unification

The item both the Pub/Sub and Scripting sections above forward-referenced as
"still doesn't exist": project spec §10's "a top-level `IRedisClient`...
abstraction with the same shape for standalone, Cluster, and
Sentinel-discovered connections." Investigating it directly (rather than
just writing the interface) surfaced that `ClusterClient` was missing
`ExecuteBlockingAsync` entirely — no cluster-aware blocking-command support
existed — so a real interface would have been dishonest about what
`ClusterClient` could actually do until that was fixed first.

- **The interface is deliberately narrow: `ExecuteAsync`, `ExecuteBlockingAsync`,
  `ExecuteBatchAsync`, `IAsyncDisposable` — nothing else.** Spec §10 draws
  this line itself: "Cluster/Sentinel-*specific* concerns... live behind
  clearly separate, opt-in surface." Two real members stay off the
  interface on exactly that basis, not by oversight: `ClusterClient`'s
  `ReadPreference`-overloaded `ExecuteAsync` (a Cluster-only concept), and
  `BeginTransactionAsync`. The second needed real thought, not just a
  category call: `RedisClient`/`SentinelClient`'s existing
  `BeginTransactionAsync(CancellationToken)` has no routing key because
  there's only ever one node, but a Cluster transaction fundamentally must
  pick a node — via a routing key — before `WATCH` even runs, since
  `RedisTransaction` leases its connection immediately at `StartAsync`
  (project spec §4). The two shapes genuinely don't unify: forcing a
  meaningless key parameter onto the single-node types is worse than not
  unifying, and having `ClusterClient`'s interface-mandated no-key overload
  throw `NotSupportedException` is worse still (an interface member that
  throws by design violates Liskov substitution — exactly the kind of
  "looks uniform, isn't" trap spec §10 is trying to prevent by calling for
  separate opt-in surface in the first place). Kept as a concrete,
  Cluster-only method instead:
  `ClusterClient.BeginTransactionAsync(ReadOnlyMemory<byte> key, CancellationToken)`.
- **`ClusterClient.ExecuteBlockingAsync` reuses the existing redirect loop
  rather than duplicating it** — `ExecuteWithRedirectsAsync`'s single
  hardcoded `client.ExecuteAsync(...)` call became a parameter
  (`Func<RedisClient, ReadOnlyMemory<byte>, ReadOnlyMemory<byte>[], CancellationToken, ValueTask<RedisResult>>`),
  so the entire MOVED/ASK/TRYAGAIN/CLUSTERDOWN/quarantine handling is
  shared, unmodified, between an ordinary command and a blocking one. This
  is correct, not just convenient: a blocking command sent to the wrong
  node still gets an immediate `MOVED` before it ever blocks (routing
  happens before the block starts), so it needs exactly the same
  redirect-then-retry behavior as anything else — the only difference is
  which of `RedisClient`'s two methods finally gets called once a healthy
  target node is resolved. Always `ReadPreference.PrimaryOnly`: every
  blocking command in the vendored table is a write (it removes or moves an
  element), never read-only, so there's no replica-eligible case to route
  to in the first place.
- **`ClusterClient.BeginTransactionAsync(key, ct)` resolves the node and
  hands back an ordinary `RedisTransaction`, no Cluster-specific wrapper
  type** — same "reuse the existing routing knowledge" pattern as
  `CreateShardSubscriberAsync` (`HashSlot.Compute` + `_topology.FindOwner`),
  then delegates straight to that node's own `RedisClient.BeginTransactionAsync`.
  Deliberately no client-side validation that the transaction's later
  `WATCH`/queued commands all stay within the resolved key's slot — tracking
  every key touched across several independent calls would be a materially
  bigger feature, and the server's own slot-ownership enforcement already
  rejects a key the connected node doesn't own, matching this project's
  existing precedent (the CROSSSLOT check) of only validating client-side
  what a single call can see, not re-implementing every server-side
  protection ahead of the server.
- **Finishing the interface let a real piece of duplication collapse**:
  `RedisScript`'s three near-identical per-client adapter extension methods
  (`RedisScriptClientExtensions`/`ClusterScriptingExtensions`/
  `SentinelScriptingExtensions`, from the Scripting phase, built as three
  separate adapters specifically because `IRedisClient` didn't exist yet)
  became one — `RedisScriptClientExtensions.EvaluateAsync(this RedisScript, IRedisClient, ...)`
  — with the two Cluster/Sentinel-specific files deleted outright.
  `RedisScript.EvaluateAsync` itself still takes the lower-level
  `RedisCommandExecutor` delegate, not `IRedisClient` directly, so
  `RedisScript` stays decoupled from any particular client abstraction; the
  one surviving adapter is the seam between the two. Every existing
  Scripting test (`ScriptingTests`, `ClusterScriptingTests`) passed
  unchanged after the collapse — no test code needed to change, since they
  already called `script.EvaluateAsync(client, ...)` and overload
  resolution picked the new single adapter transparently.
- **`IRedisClientTests` is split into three collection-scoped classes, one
  per fixture type** (`IRedisClientTestsStandalone`/`...Cluster`/
  `...Sentinel`), each calling the same shared
  `IRedisClientTests.SetAndGetAsync(IRedisClient, key)` helper — an xUnit
  test class belongs to exactly one `[Collection]`, so three fixture types
  means three classes, matching how this suite already splits other
  cross-cutting concerns. This is the actual proof of the whole feature:
  identical code, unmodified, running against a real `RedisClient`, a real
  `ClusterClient`, and a real `SentinelClient` through the interface type
  alone, against three separate live Testcontainers-managed topologies —
  not just "it compiles against the interface," which would prove nothing
  about whether the three implementations actually agree on behavior.

### Recorded during implementation of the string-argument analyzer/code fix

Prompted by a question about why every example in this project's own docs
writes `"key"u8.ToArray()` instead of a plain string: the raw escape hatch
(`IRedisClient.ExecuteAsync`/`ExecuteBlockingAsync`) only takes
`ReadOnlyMemory<byte>`, correct for the zero-allocation goal but clunky for
a literal command. Added a `string`-based overload (`StringCommandExtensions`,
`src/ReadUs.Core/StringCommandExtensions.cs`) plus a Roslyn analyzer/code
fix that nudges an all-literal call at that overload toward the byte-based
form — convenient by default, one step from optimal wherever the compiler
can actually verify the rewrite is safe.

- **The overload lives only on the raw escape hatch, as `IRedisClient`
  extension methods — not threaded through the ~412 generated typed
  methods.** Doubling `SetAsync`/`GetAsync`/etc. with string-taking twins
  would be a far bigger, more invasive change for a tradeoff (an
  unconditional runtime UTF-8 encode) this project has deliberately
  avoided everywhere else. Living on `IRedisClient` means `RedisClient`/
  `ClusterClient`/`SentinelClient` all get it from one implementation —
  the same reuse `RedisScript.EvaluateAsync` already gets from the
  interface.
- **Shape matches the byte overload exactly (`string commandName, string[]? args = null`), not `params string[] args`** —
  deliberately, so the code fix is a pure per-literal rewrite with no
  argument-list restructuring: overload resolution re-picks the byte-based
  overload automatically once every argument's compile-time type changes
  from `string`/`string[]` to `byte[]`/`ReadOnlyMemory<byte>[]`.
- **A real architectural mistake, caught by the build itself, not
  planning**: the analyzer and code fix were originally both going into
  the existing `ReadUs.SourceGenerators` project, reusing the analyzer
  reference every consumer already has. Adding
  `Microsoft.CodeAnalysis.CSharp.Workspaces` (which `CodeFixProvider`
  needs) to that assembly triggered `RS1038` — a real warning, not a
  nitpick: that assembly also hosts actual source generators, which must
  stay loadable in constrained compiler-only hosts where the Workspaces
  assembly isn't available, and a Workspaces reference there risks the
  *whole* assembly, generators included, failing to load in that
  scenario — silently breaking command-table generation, not just the new
  feature. Fixed by splitting the code fix into its own project
  (`ReadUs.SourceGenerators.CodeFixes`), the standard Roslyn
  analyzer/codefix pairing pattern; the `DiagnosticAnalyzer` itself has no
  Workspaces dependency and stayed in `ReadUs.SourceGenerators`.
- **The analyzer flags a call only when every string argument is a
  compile-time literal — nothing partial.** A call with any non-literal
  string argument (a variable, interpolation, concatenation) isn't
  touched at all; partial-literal rewriting is a real but lower-value
  case this analyzer bails out of cleanly rather than attempting a
  general solver, the same scope discipline `HashModelParser`/
  `CommandTableGenerator` already apply elsewhere in this project.
- **A real bug in the code fix, caught by its own test, not by review**:
  the first version rewrote only the literal expressions in place and
  left a single-argument call (`client.ExecuteAsync("PING")`, relying on
  the string overload's `args = null` default) with only one argument
  after rewriting the command name — but the byte-based overload's `args`
  parameter has no default, so `client.ExecuteAsync("PING"u8.ToArray())`
  alone doesn't compile (`CS1503`). `StringLiteralArgsAnalyzerTests`
  caught this immediately, since it asserts the *exact* fixed source
  (compiled, not just diffed) rather than only "no diagnostic remains."
  Fixed by having the code fix append an explicit `[]` whenever the
  original call omitted `args` entirely.
- **The Roslyn testing SDK's `XUnitVerifier` (`Microsoft.CodeAnalysis.Testing.Verifiers.XUnit`,
  latest published version 1.1.2) is marked obsolete upstream and, in
  practice, binary-incompatible with the modern `xunit.assert` package
  this project already uses elsewhere** — calling it threw
  `MissingMethodException` on a mismatched `Xunit.Sdk.EqualException`
  constructor the moment a test actually needed to compare a diagnostic
  message, not at compile time. Switched to
  `Microsoft.CodeAnalysis.Testing.DefaultVerifier` (test-framework-agnostic,
  not obsolete, no such dependency) instead, dropping the `.XUnit`-suffixed
  testing packages entirely in favor of their base equivalents.
- **Same pre-NuGet analyzer-propagation gap already documented for
  `ReadUs.Extensions.Hashes`, confirmed to apply here too**: a project
  only gets this analyzer running against its own code with its own
  direct `OutputItemType="Analyzer"` reference to `ReadUs.SourceGenerators`
  — a plain `ProjectReference` to `ReadUs.Core` does not transitively
  propagate `ReadUs.Core`'s own analyzer reference downstream. Resolves
  itself for free once these packages are actually published to NuGet (the
  `analyzers/dotnet/cs/*.dll` convention auto-wires for every consumer);
  not worth solving before that, just documented plainly in
  `docs/guides/getting-started.md`.

### Recorded during implementation of batched connection writes

Prompted by `docs/benchmarks.md`'s own honest, previously-unexplained
finding: ReadUs was faster than StackExchange.Redis on single-command round
trips but StackExchange.Redis pulled far ahead at high pipeline depth
(~7.7x at depth 512). Root-caused with real evidence before any code
changed, not guessed at: temporary instrumentation against a live server
showed `RedisConnection`'s old design called `PipeWriter.FlushAsync` once
per command, always, at every concurrency level tested — the single
`_writeGate` semaphore was held across the *entire* write-and-flush, so no
two commands were ever coalesced into one flush regardless of how many
were genuinely in flight at once.

- **A first fix attempt measured as producing zero improvement, and that
  negative result changed the design.** Letting whoever already held the
  gate also drain any *other* currently-queued items before flushing
  (still one shared gate, no new architecture) showed no batching at all
  in the same instrumented measurement. Root cause: the standard
  "fire N tasks in a loop, then `Task.WhenAll`" concurrency pattern this
  project's own benchmarks use doesn't actually produce concurrent
  *writers* — an uncontended `SemaphoreSlim.WaitAsync()` and a loopback
  `PipeWriter.FlushAsync()` both usually complete synchronously, so each
  call's entire write-and-flush finishes before the driving loop even
  starts the next one. There was nothing for a reactive, "batch whoever's
  already waiting" scheme to batch with.
- **The real fix needed a dedicated background write loop, symmetric to
  the read loop this section's own opening paragraph already described**
  (line 48 — the original design doc envisioned this shape before any of
  the current single-semaphore implementation existed; this change is
  fulfilling that original description, not deviating from it). Producers
  now only ever enqueue a `(commandName, args, PendingRequest?)` tuple
  into a `ConcurrentQueue` (`_writeQueue`) and signal a `SemaphoreSlim`
  (`_flushSignal`); `WriteLoopAsync`, one instance per connection for its
  whole lifetime, is the *only* code path that ever touches the
  connection's `PipeWriter` (new Invariant I7), draining everything
  currently queued before a single flush. Measured directly: depth-512
  elapsed time dropped from ~5.3ms to ~1.7ms (~3.2x) in the validating
  prototype, with flush count dropping from 512 to ~358 and batch sizes up
  to 15 — see `docs/benchmarks.md` for the real, final numbers from the
  actual shipped implementation.
- **A genuine TOCTOU race, found by a stress test under real load, not by
  inspection or code review.** The first complete implementation passed
  the full test suite repeatedly in isolation, but intermittently
  (roughly 1 run in 6–10) hung *only* when run as part of the full suite
  under heavier concurrent Docker load — exactly the situation this
  project's own precedent (`ClientSideCacheTests`'s poll-budget widening)
  had already taught to take seriously rather than dismiss as noise.
  Widening the new test's own timeout to 90 seconds still hung, which
  ruled out "just slow under load" and confirmed a genuine lost request.
  Temporary per-request diagnostic tracking (same technique as the
  Toxiproxy wait-strategy investigation earlier in this document) pinned
  it to exactly 2 of 200 concurrent requests, never completing. Root
  cause: `WriteLoopAsync`'s per-item move — `_writeQueue.TryDequeue`
  followed by `_pending.Enqueue` — has a real (if narrow) window between
  the two calls where a request is observable in *neither* queue. If
  `FaultAsync` (triggered concurrently and independently by the read loop
  detecting the same connection failure) ran its combined drain of both
  queues during exactly that window, it would miss the request entirely —
  never in `_writeQueue` (already removed) and not yet in `_pending`
  (not yet added). A second, related gap existed even before that: a
  request enqueued into `_writeQueue` after `FaultAsync`'s one-time drain
  had already fully completed (idempotency guard means it never runs
  again) had nothing left to ever complete it.
  - Fixed with two complementary changes, both necessary, neither
    sufficient alone: (1) a re-check immediately after enqueueing —
    `WriteAndEnqueueAsync` checks `State` again right after its own
    enqueue, and self-completes the request with a
    `RedisConnectionException` if the connection is no longer `Ready`,
    closing the "enqueued after the one-time drain already ran" gap; and
    (2) a small `Lock` (`_writeQueueMoveLock`) held only around the
    "dequeue-then-claim" move itself — never across `WriteCommand`'s
    batch or `FlushAsync` — with `FaultAsync` holding the *same* lock
    across its own combined drain of `_pending` and `_writeQueue`, so the
    two can never observe a request that's transiently in neither. Both
    are safe to layer on top of each other and on top of `FaultAsync`'s
    own drains: `PendingRequest.TryCompleteWithException` is already
    idempotent-safe against being reached by more than one path (the
    same guarantee the H1/H2 proof for blocking-command cancellation
    already depends on), so whichever path reaches a given request first
    simply wins, harmlessly.
  - Both new invariants — **I7 — Single active writer** and **I8 — No
    write-queue stranding** — are recorded in §1.5 above, alongside I4's
    extension to cover this second queue.
- **A second, unrelated pre-existing bug surfaced by the same stress
  test, fixed along the way.** `CloseAsync`'s `PipeWriter.CompleteAsync()`
  call tries to flush any still-buffered bytes as part of completing —
  which throws if the transport is already broken, a case that got
  substantially more likely under the new batching design (a batch can
  legitimately have more unflushed bytes sitting in the writer at any
  given instant than the old one-flush-per-command design ever did). The
  exception escaped `CloseAsync` uncaught, crashing whichever caller was
  awaiting `FaultAsync` (surfacing as an unrelated-looking failure from
  `RedisClient.DisposeAsync()`) *and* leaking the socket, since disposal
  never got a chance to run. Fixed by wrapping both `CompleteAsync` calls
  in `CloseAsync` with a broad, deliberately-silent catch — matching this
  project's existing precedent (e.g. `ClusterClient.DisposeAsync`'s own
  node-client cleanup loop) for "a failure during best-effort teardown of
  an already-dead resource must never prevent the actual disposal it's
  wrapping."
- **The new `SendSubscriptionCommandAsync` (Pub/Sub) folds into the same
  write-loop mechanism rather than keeping its own separate write+flush,
  a deviation from the original plan for this phase.** Necessary for
  Invariant I7 to be literally true (only one code path ever touches
  `_writer`) rather than carrying a documented, narrow exception for
  subscription traffic. Its confirmation-tracking `TaskCompletionSource`
  is already registered by `RedisSubscriber` before the send is even
  attempted, and that type's own `OnFaulted`-driven cleanup already fails
  every pending confirmation on any connection fault independent of
  low-level `_writeQueue` mechanics — so unlike an ordinary command's
  `PendingRequest`, a subscription command has no analogous "stranding"
  failure mode to close, and needed no equivalent of the
  `WriteAndEnqueueAsync` post-enqueue re-check.
- **New, permanent metrics** (`readus.connection.flushes`,
  `readus.connection.flush.batchsize` — `ReadUsDiagnostics`) are the
  direct, kept version of the throwaway counters originally used to
  diagnose and validate this whole investigation, on the reasoning that
  seeing real batch sizes for a real workload is useful on its own, not
  just for this one investigation.
