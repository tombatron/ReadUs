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
