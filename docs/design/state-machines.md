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
  what makes I3 provable rather than "usually true."
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

Not covered by this pass, deliberately: a client-wide default read
preference (see "Surface" above); latency-aware replica selection
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

- **Replica read routing is not implemented.** `ClusterTopology` only tracks
  each shard's master (project spec §5's own "primary-only, safest" default
  policy) — `CLUSTER SHARDS` already reports replicas too, so adding
  prefer-replica/replica-only/round-robin routing later is a matter of
  tracking the rest of each shard's `nodes` array, not re-discovering it.
- **No per-node health tracking, quarantine, or ejection.** A node that goes
  down surfaces as an ordinary `RedisConnectionException` from whatever call
  hit it; there's no background health-checker ejecting a repeatedly-failing
  node's connections or reintegrating one that recovers. §5 asks for this
  explicitly — deferred, not forgotten.
- **No explicit per-node pipeline batching.** Concurrent calls through
  `ClusterClient.ExecuteAsync` still pipeline within whatever node's Tier 1
  pool they land on, but there's no batch API that fans a single logical
  batch out across multiple nodes by destination and reassembles replies in
  the caller's original order (§5's "pipelining means batching per-
  destination-node, not per-call"). Worth building once there's a
  non-cluster batch API to extend in parallel — right now there isn't one
  for the standalone client either.
- **`ConcurrentDictionary<string, Task<RedisClient>>.GetOrAdd` can race under
  contention**: two callers discovering the same new node for the first time
  simultaneously can each start a connect, with the loser's `RedisClient`
  silently discarded (never returned, never disposed) rather than reused.
  Rare (only matters the first time a given node is contacted) and not a
  correctness bug — `RedisClient`/`RedisConnection` clean up their own
  sockets on GC finalization paths notwithstanding, this is still a minor
  resource-efficiency gap. A `SemaphoreSlim`-per-key or `Lazy<Task<T>>`
  wrapper would close it if it ever shows up in practice.
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
- **The live-cluster fixture for `ClusterClientTests` is three manually
  Docker-launched `redis-server` processes** (host networking, ports
  7001-7003, bootstrapped via `redis-cli --cluster create`), not yet the
  Testcontainers-based throwaway harness project spec §9.2 calls for. It's
  intentionally left running (like the existing standalone dev server) so
  the tests stay runnable across sessions; folding it into a proper
  disposable Testcontainers fixture is part of the fault-injection work this
  project still owes for Cluster and Sentinel.

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
- **Only default (non-`BCAST`) tracking mode is implemented.** `BCAST`
  mode, key-prefix tracking, and redirected tracking (`CLIENT TRACKING
  REDIRECT`) are mentioned only in passing by the project spec and are not
  implemented; `ClientSideCache` assumes one dedicated connection tracks
  exactly the keys it has personally read.
- **Higher-level typed helpers (POCO mapping) are deliberately out of
  scope for this pass.** The project spec frames these as optional/lowest
  priority within the convenience-layer step, behind DI, metrics, and
  client-side caching; not attempted here.

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
