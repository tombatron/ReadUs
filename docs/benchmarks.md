# Benchmarks

Real numbers from a real run, not aspirational figures — see
[Methodology](#methodology) for exactly how these were produced, and
reproduce them yourself before relying on any of this for a decision that
matters. This is a single machine at a single point in time, not a
universal claim about either library.

## Environment

- BenchmarkDotNet v0.15.8, default job (BenchmarkDotNet's own adaptive
  iteration count, not a shortened one).
- AMD Ryzen 9 5900X, Linux, 24 logical / 12 physical cores.
- .NET SDK 10.0.401, .NET runtime 10.0.12, RyuJIT x86-64-v3.
- `redis:8.10.1` in Docker, default configuration, on the same machine
  (`localhost:6379`) — no network hop, so these numbers are a ceiling on
  what a real deployment (client and server on separate hosts) would see,
  not a floor.
- StackExchange.Redis 2.9.25, via `ConnectionMultiplexer.ConnectAsync`,
  compared like-for-like: same server, same connection count, same
  payload bytes.

## ReadUs vs. StackExchange.Redis: single-command latency

One connection each, one command at a time (pipeline depth 1) — pure
protocol/transport overhead, no concurrency. `Ratio` is
`[library]/[ReadUs]`; `Alloc Ratio` likewise.

| Operation | Payload | ReadUs (mean) | StackExchange.Redis (mean) | Ratio | ReadUs alloc | SE.Redis alloc |
|---|---:|---:|---:|---:|---:|---:|
| GET | 16 B | 45.08 us | 70.62 us | 1.57 | 656 B | 384 B |
| GET | 256 B | 45.38 us | 68.90 us | 1.52 | 896 B | 624 B |
| GET | 4096 B | 48.85 us | 74.43 us | 1.52 | 4736 B | 4464 B |
| SET | 16 B | 46.37 us | 69.96 us | 1.51 | 664 B | 328 B |
| SET | 256 B | 46.00 us | 68.98 us | 1.50 | 664 B | 328 B |
| SET | 4096 B | 54.10 us | 77.66 us | 1.44 | 664 B | 328 B |

ReadUs is consistently ~1.44-1.57x faster on single-command round trips
across every payload size tested. It also consistently allocates *more*
per call than StackExchange.Redis does here — worth stating plainly
rather than only reporting the flattering number: StackExchange.Redis's
`RedisValue`/`RedisKey` types are more allocation-frugal for these small
payloads than ReadUs's current `RedisResult` (which always copies a
scalar reply's bytes into a fresh heap array — see
[Architecture](guides/architecture.md#the-zero-allocation-goal)) in this
specific comparison, even though ReadUs is faster in wall-clock terms.

## ReadUs vs. StackExchange.Redis: pipelined throughput

`PING` fan-out at increasing concurrent depth, 4 connections each
(`Task.WhenAll` over that many concurrent calls before awaiting any of
them).

| Depth | ReadUs (mean) | StackExchange.Redis (mean) | Ratio |
|---:|---:|---:|---:|
| 1 | 43.98 us | 64.51 us | 1.47 |
| 8 | 98.23 us | 84.65 us | 0.86 |
| 64 | 154.11 us | 143.95 us | 0.93 |
| 512 | 425.00 us | 531.65 us | 1.25 |

This used to be the honest, less flattering half of the picture —
StackExchange.Redis pulled substantially ahead as pipeline depth grew, by
roughly 7.7x at depth 512. That gap has since been root-caused and closed.
The old `RedisConnection` held a single write gate across an entire
write-and-flush cycle, so nothing was ever coalesced into one flush
regardless of how many commands were genuinely in flight; it was replaced
with a dedicated per-connection background write loop that decouples the
(cheap, instant) enqueue from the (batched) flush. The full story —
including a first fix attempt that measured as producing zero
improvement, and two real concurrency bugs found and fixed via stress
testing along the way — is recorded in
[`docs/design/state-machines.md`](design/state-machines.md#recorded-during-implementation-of-batched-connection-writes).
Reported as measured, not as a victory lap: depth 512 flips from
StackExchange.Redis being ~7.7x faster to ReadUs being ~1.25x faster: a
change of roughly an order of magnitude in the ratio, not just a modest
improvement. Depth 8 and 64 close from ReadUs being meaningfully slower
(0.67x, 0.25x) to close to parity, still very slightly behind (0.86x,
0.93x) — worth naming plainly rather than rounding up to "solved
everywhere."

## ReadUs solo: round-trip latency by payload size

`Ping`/`Get`/`Set` at pipeline depth 1, one connection, `Ping` as
baseline.

| Operation | Payload | Mean | Ratio vs. Ping | Allocated | Alloc ratio vs. Ping |
|---|---:|---:|---:|---:|---:|
| Ping | 16 B | 43.03 us | 1.00 | 456 B | 1.00 |
| Get | 16 B | 43.64 us | 1.01 | 592 B | 1.30 |
| Set | 16 B | 44.27 us | 1.03 | 600 B | 1.32 |
| Ping | 256 B | 43.24 us | 1.00 | 456 B | 1.00 |
| Get | 256 B | 43.81 us | 1.01 | 832 B | 1.82 |
| Set | 256 B | 44.92 us | 1.04 | 600 B | 1.32 |
| Ping | 4096 B | 43.36 us | 1.00 | 456 B | 1.00 |
| Get | 4096 B | 48.27 us | 1.11 | 4672 B | 10.25 |
| Set | 4096 B | 52.46 us | 1.21 | 600 B | 1.32 |

`Set`'s allocation stays flat regardless of payload size — its reply is
always just `+OK`, independent of how much data was written. `Get`'s
allocation scales with the payload, because `RedisResult` copies a bulk
reply's bytes out of the connection's read buffer exactly once, at the
point the value has to leave the read loop (see
[Architecture](guides/architecture.md#the-zero-allocation-goal)) — that
copy is unavoidable and scales with what's actually being read.

`Ping`'s own allocation floor moved from 368 B to 456 B, a real (if
small) per-call cost of the batched-write redesign described above: every
command now goes through a `ConcurrentQueue` enqueue rather than writing
straight to the socket, even when nothing else is queued alongside it.
That fixed ~88 B cost buys the pipelined-throughput improvement in the
section above; it isn't free, and it's reported here rather than left
out.

## ReadUs solo: pipelined `PING` throughput

Same shape as the comparative pipeline table above, ReadUs only —
included for consistency with the two dedicated ReadUs-only benchmark
classes this repo has had from early on.

| Depth | Mean | Allocated |
|---:|---:|---:|
| 1 | 43.64 us | 656 B |
| 8 | 98.79 us | 3629 B |
| 64 | 154.23 us | 24690 B |
| 512 | 426.05 us | 193165 B |

(Matches the `ComparativePipelineDepthBenchmarks` ReadUs column above
within noise, as it should — same code path, independently run.)

## Methodology

- `dotnet run -c Release --project benchmarks/ReadUs.Benchmarks` runs the
  full suite (`RoundTripLatencyBenchmarks`, `PipelineDepthBenchmarks`,
  `ComparativeGetSetBenchmarks`, `ComparativePipelineDepthBenchmarks`).
  Pass `--filter "*ClassName*"` to run just one.
- All four classes expect a plain `redis-server` reachable at
  `localhost:6379` — nothing containerized or Testcontainers-managed here
  (unlike the test suite), since benchmark numbers need a stable,
  predictable environment, not a fresh one per run.
- BenchmarkDotNet's default job was used throughout — no `--job short`
  shortcuts feeding these particular numbers; the quick sanity checks run
  while developing the comparative benchmarks used a short job and are
  not what's reported here.
- Every comparative pairing uses the same connection count, same server,
  same payload bytes on both sides — see
  `ComparativeGetSetBenchmarks`/`ComparativePipelineDepthBenchmarks`'s own
  source for the exact setup.

## See also

- [Architecture](guides/architecture.md) — what the zero-allocation goal
  actually covers and where it deliberately doesn't apply.
