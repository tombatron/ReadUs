# Project ReadUs — Design Prompt for a High-Performance .NET Redis Client

> Paste this entire document into a fresh Claude Code session as its starting instructions. It is self-contained: it does not assume the receiving session has seen any prior conversation.

---

## 0. Mission Statement

Design and build **ReadUs**, a from-scratch .NET client library for Redis whose entire reason for existing is performance, predictable low allocation, and provable stability — not feature-for-feature parity dressed up with a nicer API. ReadUs must be a complete, production-grade replacement for StackExchange.Redis, closing that library's known gap on blocking commands, while adding first-class Cluster and Sentinel support and targeting connectivity to managed/remote Redis (AWS ElastiCache/MemoryDB, Azure Cache for Redis, Google Memorystore, Redis Cloud).

This is a greenfield project. There is no existing ReadUs code to preserve or migrate — you are free to choose the cleanest architecture that satisfies the constraints below. Where a design tradeoff exists, favor the option that is faster and more predictable at the cost of surface-level convenience; ergonomics matter but never outrank the performance/stability mandate.

## 1. Target Platform and Redis Version

- **Primary target framework:** .NET 10 (assume it is available; use every runtime, BCL, and language feature it offers — do not write code as if you were still targeting .NET 8 "for safety"). Use C# at whatever language version ships with .NET 10.
- **Primary target server:** the current Redis major version line (8.x as of this writing, RESP2 **and** RESP3 wire protocols, `HELLO`-based handshake negotiation). Do not special-case around Redis 6/7 behavior in the core implementation — assume the latest server semantics and command table.
- Multi-targeting older .NET versions or older Redis majors is explicitly **out of scope for the initial build**. The architecture should not make that support impossible later (see §11), but do not spend design or implementation effort on it now. If a design choice would be free either way, prefer the one that keeps a future compatibility branch cheap; never trade current performance for it.

## 2. Non-Negotiable Constraints

1. **Full command coverage, including every blocking command.** `BLPOP`, `BRPOP`, `BLMOVE`, `BLMPOP`, `BRPOPLPUSH`, `BZPOPMIN`, `BZPOPMAX`, `BZMPOP`, `WAIT`, `WAITAOF`, blocking `XREAD`/`XREADGROUP` (the `BLOCK` option), and blocking behavior on streams/consumer groups must all be supported as first-class, cancellable async operations. This is the specific gap StackExchange.Redis leaves open (it multiplexes all commands over shared connections and cannot let one logical call block a physical connection indefinitely), so your connection architecture must solve this deliberately — see §4.
2. **Cluster mode.** Full slot-map-aware routing, redirection handling, and topology maintenance — see §5.
3. **Sentinel mode.** Master discovery, failover detection, and automatic re-routing — see §6.
4. **Remote/managed Redis.** TLS, SNI, non-default auth flows (AWS IAM auth tokens, Azure Entra ID/AAD token auth, username+password ACL auth, plain password auth), DNS peculiarities of managed endpoints — see §7.
5. **Speed.** Competitive with or faster than StackExchange.Redis and NRedisStack on raw request/response latency and throughput, at all pipeline depths from 1 to deeply pipelined.
6. **Minimal, predictable allocation.** Steady-state command execution (the hot path: serialize request → write → read reply → deserialize) should be allocation-free or as close to it as the type system allows. Allocations should scale with payload size, not with call count.
7. **Provable stability.** "Provable" here means: the connection/reconnection/cluster-redirect/blocking-cancellation state machines are specified precisely enough to be model-checked or exhaustively tested, and the test suite actually exercises them under fault injection — not just "we have unit tests." See §9.

## 3. Redis Command Surface (ground truth)

Redis's own source tree is the source of truth for the command table: `src/commands/*.json` (currently 465 files, one per command/subcommand, including things like `cluster-shards.json`, `client-tracking.json`, `waitaof.json`). Each file declares argument structure, flags (`WRITE`, `READONLY`, `BLOCKING`, `MOVABLEKEYS`, ACL categories, since-version, etc.) in a structured schema.

**Do not hand-write the command surface.** Build a build-time source generator (or a checked-in codegen step run against a vendored copy of `commands.def`/the JSON command table) that ingests this table and emits:
- Strongly-typed method stubs per command (grouped by data type: String, Hash, List, Set, Sorted Set, Stream, HyperLogLog, Bitmap, Geo, Scripting/Functions, Pub/Sub, Transactions, Connection, Server, Cluster, ACL, Client).
- Argument validation matching each command's declared arity/flags at compile time where possible, at dispatch time otherwise.
- Metadata used for routing: which arguments are keys (including commands with `MOVABLEKEYS`, e.g. `SORT`, `ZADD` variants, `GEORADIUS`), which commands are `BLOCKING`, which are read-only (for replica routing).

This gives you two important properties: (a) the client stays in sync with the server automatically when you regenerate against a newer command table instead of manually chasing changelogs, and (b) you get a systematic way to reason about test coverage — every generated command needs a corresponding integration test.

In addition to the generated typed surface, provide a **low-level escape hatch** — a raw `ExecuteAsync(ReadOnlyMemory<byte> commandName, params ReadOnlyMemory<byte>[] args)`-style API (exact shape is your call) that can send any command, including ones the generator doesn't know about yet, and including module commands (`FT.*` RediSearch, `JSON.*`, `TS.*`, Bloom/Cuckoo/TopK/Count-Min-Sketch `BF.*`/`CF.*`/`TOPK.*`/`CMS.*`). These modules ship inside the Redis 8.x distribution but are not part of `src/commands/*.json`, so they must go through the escape hatch or a separately generated module command layer — do not block the core client's release on them, but do not make them awkward to reach either.

## 4. Connection Architecture — the part that makes blocking commands work

StackExchange.Redis's multiplexer model (many logical calls time-shared over one physical connection per node) is why it cannot support blocking commands: a blocked physical connection can't service anything else queued behind it. ReadUs must support both usage patterns without compromising either.

Design a **two-tier connection model per logical endpoint (a standalone node, or a single node inside a cluster/sentinel topology):**

- **Tier 1 — Multiplexed pipeline pool.** One or more physical connections, each running an independent read loop and write loop (`System.IO.Pipelines`-based), used for all *non-blocking* request/response traffic and for explicit client-side pipelining/batching. Requests are correlated to responses by strict FIFO ordering per connection (Redis guarantees in-order replies), so completion is a queue of awaiters/continuations, not a dictionary keyed by request ID. Scale the number of multiplexed connections based on configured concurrency, not per-command.
- **Tier 2 — Leased/dedicated connections.** A pool of connections that are checked out exclusively for the duration of one logical operation and returned afterward. Use this tier for:
  - Every `BLOCKING`-flagged command (per the generated metadata from §3).
  - `MULTI`/`EXEC`/`WATCH` transactions (session/connection affinity is required for correctness — a `WATCH` on one physical connection then an `EXEC` on another is meaningless).
  - `SUBSCRIBE`/`PSUBSCRIBE`/`SSUBSCRIBE` sessions (a subscribed connection can only speak the subscribe sub-protocol in RESP2; RESP3 relaxes this via push messages but dedicating the connection is still the simpler, safer default).
  - Blocking `XREAD`/`XREADGROUP` calls.

  Dedicated connections must support **true cancellation without corrupting the pool**: if a caller cancels a `BLPOP` via `CancellationToken`, ReadUs must either (a) issue `CLIENT UNBLOCK <id> [TIMEOUT|ERROR]` on a side channel to unblock the server-side client and then safely return the connection to the pool, or (b) close and discard that physical connection rather than return a connection whose reply stream is out of sync. Never return a connection to the pool with unread/ambiguous state on it. Pick a policy, document it precisely, and test it under cancellation races (cancel-right-before-reply-arrives, cancel-after-reply-already-in-flight-on-the-wire).

- Both tiers should share the same transport primitives (frame writer/reader, socket setup, TLS negotiation, auth handshake) — the tiering is a pooling/lifetime policy layered on common connection machinery, not a second protocol stack.
- Timeout handling: every operation needs an independent, cancellable timeout distinct from the blocking command's own server-side timeout argument (e.g., `BLPOP key 5` blocks server-side for 5s; the client-side `CancellationToken`/deadline is a separate, composable concern and must be honored even mid-block).

## 5. Cluster Mode

Ground truth from the Redis source (`src/cluster.c`) and the cluster spec:

- 16384 hash slots. Slot for a key is `CRC16(key) % 16384`, with **hash tag** support: if the key contains `{...}`, only the substring between the first `{` and the next `}` is hashed (empty or missing braces fall back to hashing the whole key). Implement this exactly — off-by-one hash-tag parsing is a classic client bug.
- Topology discovery: use `CLUSTER SHARDS` (preferred, richer — gives you slot ranges per shard plus node health/replication-offset-ish data) and/or `CLUSTER SLOTS`/`CLUSTER NODES` as a fallback for older behavior compatibility. Build the slot→node map at startup from any seed node and refresh it:
  - Periodically (configurable interval) as a background health check.
  - Immediately and reactively whenever a `MOVED` or `CLUSTERDOWN` response is observed, or when a configured number of consecutive connection failures to a node occurs.
- Redirection handling:
  - **`MOVED <slot> <host>:<port>`** — the slot has permanently migrated. Update the local slot map (at least for that slot; trigger a fuller topology refresh in the background) and retry the command against the new owner. Do not retry indefinitely — cap redirects per logical call and surface an error if the map won't stabilize.
  - **`ASK <slot> <host>:<port>`** — the slot is *mid-migration*. Send `ASKING` then the original command to the target node, but do **not** update the persistent slot map from an ASK (it's provisional).
  - **`TRYAGAIN`** — transient, back off briefly and retry the same node.
  - **`CROSSSLOT`** — a multi-key command spans slots the client should have caught before sending; treat this primarily as a client-side validation bug to catch pre-flight (compute the slot for every key argument up front, including `MOVABLEKEYS` commands, and refuse to send cross-slot multi-key commands client-side with a clear exception, rather than relying on the server to reject them) — but still handle the wire-level error defensively.
  - **`CLUSTERDOWN`** — surface distinctly from ordinary connection failures; back off longer.
- Multi-key command validation must use the same key-extraction metadata generated in §3 (including movable-keys commands like `SORT ... STORE`, `GEORADIUS ... STORE`, `ZADD`/`EVAL`/`EVALSHA` with explicit key counts, `XREAD`/`XREADGROUP` with multiple streams) — do not maintain a second, hand-written list of "which args are keys" that can drift from the generator.
- Pipelining across a cluster means batching per-destination-node, not per-call: when a caller pipelines a batch of commands touching different slots, ReadUs should fan the batch out to the right node connections and reassemble responses in the caller's original order.
- Read routing policy should be configurable per call or per client: primary-only (default, safest), prefer-replica, replica-only, or a round-robin/latency-aware policy across primary+replicas for a shard. Reads routed to a replica require `READONLY` to have been issued on that connection (and `READWRITE` to undo it) per the Redis cluster protocol — manage this transparently as part of the dedicated-vs-pooled connection lifecycle, don't leak it into the public API.
- Track per-node health (consecutive failures, latency) and support ejecting/quarantining a node's connections without tearing down the whole client, reintegrating it once it responds to `PING`/`CLUSTER INFO` again.

## 6. Sentinel Mode

- ReadUs should accept a list of Sentinel endpoints and a master/service name, and:
  1. Query each Sentinel (`SENTINEL get-master-addr-by-name <name>`) until one answers authoritatively, then connect to the reported master.
  2. Optionally discover replicas via `SENTINEL replicas <name>` (or the legacy `SENTINEL slaves <name>`) for read-routing, and discover peer sentinels via `SENTINEL sentinels <name>` to keep the sentinel address list current even as sentinels are added/removed.
  3. Subscribe (on a dedicated connection to at least one, ideally a quorum-aware subset, of the sentinels) to the `+switch-master` pub/sub channel for fast failover detection, rather than relying solely on polling. Treat a `+switch-master` message for the watched service name as the trigger to tear down the current master connection tier and reconnect to the newly announced address.
  4. Fall back to periodic polling (`SENTINEL get-master-addr-by-name`) as a safety net in case a pub/sub notification is missed (e.g., the sentinel connection itself dropped momentarily).
  5. Handle the case where sentinels disagree during a failover window (query multiple sentinels, apply a simple majority/quorum-style acceptance rather than trusting the first responder blindly) — document the exact policy chosen, since this is exactly the kind of ambiguous distributed-systems corner that needs a written, testable spec (§9) rather than "whatever the first version happened to do."
- Sentinel-managed connections still route through the same Tier 1/Tier 2 connection architecture (§4) once the current master is known — Sentinel is purely a discovery/failover layer on top, not a separate command execution path.

## 7. Remote / Managed Redis Connectivity

- **TLS**: full support via `SslStream`, including SNI, custom certificate validation callbacks (for self-signed/private CA setups common in cloud deployments), and mutual TLS (client certificates) for providers that require it.
- **Pluggable authentication**, because managed providers diverge here:
  - Static username/password (`AUTH`/`HELLO ... AUTH`), the common case and default.
  - **AWS ElastiCache/MemoryDB IAM auth**: a short-lived, periodically-refreshed auth token generated via AWS SigV4 signing that gets passed as the password on `AUTH`/`HELLO`. ReadUs shouldn't implement AWS SigV4 signing itself (avoid an AWS SDK hard dependency in core) — instead define a `ICredentialsProvider`/`IRedisCredentials` abstraction with an async "get current credentials, refresh if near expiry" contract, so applications can plug in a token supplier (backed by the AWS SDK, Azure SDK, or their own code) and ReadUs just re-authenticates connections when credentials rotate, including live re-`AUTH` on already-open connections where the server supports it, and reconnect-with-new-credentials otherwise.
  - **Azure Cache for Redis Entra ID (AAD) token auth**: same abstraction — a bearer token as the password, refreshed on its own cadence, with the client responsible for re-authenticating proactively before expiry rather than waiting for an auth failure.
  - Credential rotation must not require tearing down the whole client — only refresh/re-auth the affected connections.
- **DNS behavior**: managed endpoints are frequently a single DNS name that can resolve to a different IP after a failover (Azure) or that intentionally round-robins/changes (AWS cluster config endpoint). Do not cache a resolved IP for the lifetime of the client — re-resolve on reconnect, and treat "connect failed" / repeated timeouts as a trigger to re-resolve rather than retrying the stale IP forever.
- Cluster-mode managed offerings (AWS ElastiCache "cluster mode enabled", Azure Cache Premium clustering) speak the standard Redis Cluster protocol described in §5 — no special-casing needed there beyond normal topology discovery from whatever seed endpoint/port the provider gives you.
- Provide first-class configuration for connect/command timeouts, keepalive, and reconnect backoff (with jitter) since cross-AZ/cross-region latency and transient managed-service blips are the norm, not the exception, for remote Redis.

## 8. Performance & Allocation Design

Use every relevant .NET 10 capability; do not hedge toward older idioms "for compatibility" since that's explicitly out of scope (§1). Specific expectations:

- **Transport**: `System.IO.Pipelines` (`PipeWriter`/`PipeReader`) over `Socket`/`NetworkStream`/`SslStream` for the read/write loops. No `StreamReader`/`StreamWriter`, no per-call `Task.Run`.
- **RESP parsing**: a zero-copy reader operating over `ReadOnlySequence<byte>`, returning `ReadOnlyMemory<byte>`/`ReadOnlySpan<byte>`-based views into pooled buffers wherever the caller can accept a non-owning view; only copy into a caller-owned array/string when the API contract requires materialization (e.g., a `string` return). Prefer exposing `RedisValue`-style lightweight structs that defer decoding (UTF8→string, integer parsing) until the caller actually asks for that representation.
- **RESP writing**: a `ref struct` command writer that serializes directly into pooled/pipeline-owned buffers — no intermediate `List<object>` or boxed argument arrays for the common typed command overloads generated in §3.
- **Buffer management**: `ArrayPool<byte>`/pooled `Memory<byte>` throughout; no per-command heap array allocation for typical key/value sizes. Make pool sizing/behavior configurable (min/max segment sizes) since workloads vary wildly (small counters vs. large blobs/streams).
- **Async without Task-per-call overhead**: use `ValueTask`/`ValueTask<T>` backed by a pooled `IValueTaskSource<T>` implementation (e.g., modeled on `ManualResetValueTaskSourceCore<T>`) for the request/response completion path, so a steady stream of commands does not allocate a `Task<T>` per call. Avoid `async`/`await` state-machine allocation on hot paths where a synchronous fast path (e.g., pipelined write with reply already buffered) is achievable — but don't hand-roll this so aggressively that correctness or readability suffers; benchmark before micro-optimizing further.
- **No LINQ, no reflection, no boxing on the hot path.** Reflection-driven "convenience" (e.g., mapping a POCO to a hash via reflection) belongs in an optional, clearly-labeled higher-level convenience package, never in the core command path, and should use source generators instead of runtime reflection if it's offered at all.
- **Avoid delegate/closure allocation** in pipelines and callback registration (pub/sub handlers, `CLIENT UNBLOCK` cancellation glue) — use static lambdas / method groups with explicit state objects passed through, not captured.
- **SIMD where it pays off**: consider `System.Numerics`/hardware intrinsics for bulk operations like scanning for line terminators (`\r\n`) in RESP frames, similar to how high-performance JSON/text parsers vectorize delimiter search — but only after profiling shows it matters; don't add intrinsics complexity speculatively.
- **`IAsyncEnumerable<T>`** for natively cursor-based/streaming server semantics: `SCAN`/`HSCAN`/`SSCAN`/`ZSCAN` cursors, and streaming consumption of `XREAD`. This should be a thin layer over the same pooled-buffer machinery, not a LINQ-heavy abstraction.
- **Native AOT / trimming friendliness**: avoid `Type.GetType`/dynamic reflection-based dispatch so the client can be used from Native AOT-published apps (a real deployment target for latency-sensitive services). Validate with an AOT smoke-test project, not just a compatibility claim in docs.
- Ship a **benchmark project** (BenchmarkDotNet) from day one covering: single command round-trip latency at varying payload sizes, pipelined throughput at varying pipeline depths, cluster-routed command overhead vs. standalone, allocation counts (via BenchmarkDotNet's memory diagnoser) for each of the above. Performance work without a benchmark harness to prove it is not acceptable for this project — the benchmarks are how "fast" and "minimal allocation" get demonstrated, not asserted.

## 9. "Provably Stable" — What That Means Here and How to Get It

"Provable" is a high bar; treat it as: *every concurrency/lifecycle state machine in this client has a written specification precise enough that a reviewer (or a model checker) can determine whether a given trace is legal, and the test suite generates and checks traces against that spec, not just happy-path unit tests.* Concretely:

1. **Write down the state machines** before implementing them, as part of the design doc this project produces: connection lifecycle (connecting → authenticating → ready → draining → closed, plus failure transitions at each stage), the blocking-command-cancellation race (§4), the cluster redirect/topology-refresh state machine (§5), and the sentinel failover state machine (§6). For at least the connection lifecycle and the blocking-cancellation race — the two most failure-prone areas — express each spec as an explicit set of states and legal transitions, then validate it with an **exhaustive in-process state-space walker written in C#**: a test harness that enumerates every reachable (state, event) combination for the machine — including interleavings of cancellation firing concurrently with a reply arriving, a pool-return racing a close, etc. — drives the real implementation through each one, and asserts the classic hazards never occur: lost wakeups, double-completion of a pending call, returning a connection to the pool in an inconsistent read position, deadlock between the cancellation path and the normal completion path. Keep this in C# rather than reaching for an external formal-methods tool (e.g., TLA+) — the goal is a harness the whole team can read, run, and extend as part of the normal test suite.
2. **Fault-injection integration tests** using a real Redis server built from this repo's source (or the system's installed `redis-server`/`redis-cli`) plus a real cluster (multiple `redis-server` instances wired with `CLUSTER MEET`) and a real Sentinel constellation (multiple `redis-sentinel` processes) — spun up via Testcontainers or an equivalent throwaway-process harness, not mocked. Inject: mid-response TCP resets, partial writes/reads (via a proxy layer like Toxiproxy or a custom byte-delaying stream wrapper), simulated `MOVED`/`ASK`/`CLUSTERDOWN` sequences, a forced Sentinel failover (`SENTINEL failover <name>` or killing the primary), and blocking-command cancellation racing the server's reply.
3. **Property-based testing** (e.g., FsCheck or a hand-rolled generator) for the RESP encoder/decoder: round-trip arbitrary command argument shapes and arbitrary (including malformed/truncated) reply byte streams, asserting the decoder never throws an unhandled exception on malformed input and never silently misparses a frame boundary.
4. **Long-running soak tests** that hammer the client for hours under realistic mixed load (including blocking commands, pub/sub, and cluster redirects happening concurrently) while tracking process memory/GC counters, to catch slow leaks that unit tests can't see — this is the practical validation of the "memory friendly" claim, same way the benchmark project validates "fast."
5. **Static rigor**: nullable reference types enabled and enforced (no warnings suppressed without a documented reason), analyzers on for `Async`/`ValueTask` misuse (double-await, double-consumption of a pooled `IValueTaskSource`), and CI gates that fail the build on new warnings.
6. Treat every bug found via the above as a reason to *tighten the written spec*, not just patch the code — the goal is a spec + test corpus that keeps growing to cover every previously-found hazard, so regressions are structurally prevented, not just individually fixed.

## 10. API Shape (illustrative, not prescriptive — use your judgment on exact naming)

- Async-first: every I/O-bound member returns `ValueTask`/`ValueTask<T>`, accepts a `CancellationToken`.
- A top-level `IRedisClient` (or similarly named) abstraction with the same shape for standalone, Cluster, and Sentinel-discovered connections — application code should not need `if (cluster) { ... } else { ... }` branches for ordinary commands; cluster-awareness is transparent. Cluster/Sentinel-*specific* concerns (explicit slot queries, per-node diagnostics, read-preference overrides) live behind clearly separate, opt-in surface.
- Transactions: a fluent `MULTI`/`EXEC`/`WATCH` builder that owns a dedicated connection for its lifetime (see §4) and is disposed deterministically.
- Pub/Sub: an `IAsyncEnumerable<RedisMessage>`-style subscription API (or channel-based, your call) covering `SUBSCRIBE`/`PSUBSCRIBE` and cluster sharded pub/sub (`SSUBSCRIBE`).
- Scripting: `EVAL`/`EVALSHA`/`FUNCTION` support with automatic script-cache-miss fallback (retry as `EVAL` on `NOSCRIPT`), and a typed wrapper so a script's declared keys/args are checked rather than passed as loose object arrays.
- RESP3-specific features exposed properly, not just tolerated: out-of-band push messages, and **client-side caching** (`CLIENT TRACKING`) as an opt-in local-cache layer with invalidation handled via RESP3 push messages — a natural fit for the "minimal allocation, fewer round trips" goal, and worth designing in from the start rather than bolting on later.
- Dependency injection integration (`Microsoft.Extensions.DependencyInjection` registration helpers) and OpenTelemetry metrics/tracing (connection pool occupancy, per-command latency, redirect counts, reconnect counts) as separate, optional packages that add zero overhead when not referenced.

## 11. Versioning and Future Compatibility (context, not in-scope work)

The person driving this project has not yet decided how older .NET runtimes and older Redis majors will eventually be supported — options under consideration include long-lived support branches. When making architectural decisions now, prefer choices that keep that door open cheaply (e.g., isolate .NET 10-only intrinsics behind small, swappable internal seams; keep the codegen'd command layer versioned by the Redis command-table snapshot it was generated from, so retargeting to an older server mostly means regenerating from an older `commands.json` snapshot rather than rewriting call sites) — but do not implement multi-targeting, feature-detection shims, or a compatibility branch now. Note any place where you had to make such a call so it's easy to revisit later.

## 12. Suggested Deliverable Structure

- `ReadUs.Core` — connection management, RESP protocol, pooling, the generated command surface, standalone client.
- `ReadUs.Cluster` — slot map, redirect handling, topology discovery, cluster client.
- `ReadUs.Sentinel` — sentinel discovery/failover client.
- `ReadUs.SourceGenerators` — the command-table codegen described in §3.
- `ReadUs.Extensions.DependencyInjection`, `ReadUs.Extensions.OpenTelemetry` — optional add-ons.
- `ReadUs.Benchmarks` — BenchmarkDotNet suite (§8).
- `ReadUs.Tests.Unit`, `ReadUs.Tests.Integration` (real server/cluster/sentinel via Testcontainers), `ReadUs.Tests.Fuzz` (property-based RESP tests), `ReadUs.Tests.Soak` (long-running memory/GC validation).
- A top-level design doc capturing the state machines from §9 before their implementations are written.

## 13. What to Do First

1. Write the connection-lifecycle and blocking-cancellation state machine specs (§9.1) before writing connection code.
2. Stand up the RESP2/RESP3 protocol layer and the Tier 1 multiplexed pool against a single standalone `redis-server`, with the benchmark project measuring it from the start.
3. Add the Tier 2 dedicated-connection pool and get every blocking command and `MULTI`/`WATCH`/`EXEC` working and fault-tested.
4. Build the command-table source generator against `src/commands/*.json` and switch the hand-tested commands over to generated code, then fill out full coverage.
5. Layer in Cluster support, then Sentinel support, then the managed-Redis auth/TLS/DNS concerns.
6. Only after the above is solid, add the convenience layers (DI integration, OpenTelemetry, client-side caching, higher-level typed helpers).

Work in that order. Do not start on Cluster/Sentinel/managed-cloud concerns before the core standalone connection architecture (including blocking commands and cancellation) is proven correct under fault injection — that core is where most of the "provably stable" and "minimal allocation" risk lives, and getting it right first makes everything layered on top of it easier to reason about.
