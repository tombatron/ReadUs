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
| GET | 16 B | 46.78 us | 69.40 us | 1.48 | 568 B | 384 B |
| GET | 256 B | 45.25 us | 68.84 us | 1.52 | 808 B | 624 B |
| GET | 4096 B | 49.90 us | 74.22 us | 1.49 | 4648 B | 4464 B |
| SET | 16 B | 45.35 us | 68.58 us | 1.51 | 576 B | 328 B |
| SET | 256 B | 44.90 us | 68.25 us | 1.52 | 576 B | 328 B |
| SET | 4096 B | 53.74 us | 72.69 us | 1.35 | 576 B | 328 B |

ReadUs is consistently ~1.35-1.52x faster on single-command round trips
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
| 1 | 44.08 us | 63.79 us | 1.45 |
| 8 | 127.95 us | 85.58 us | 0.67 |
| 64 | 581.63 us | 147.88 us | 0.25 |
| 512 | 4,077.39 us | 527.88 us | 0.13 |

This is the honest, less flattering half of the picture, reported as
measured rather than left out: ReadUs wins at low concurrency (depth 1,
matching the single-command table above) but StackExchange.Redis pulls
substantially ahead as pipeline depth grows — by depth 512, roughly 7.7x
faster in this specific scenario. **The root cause hasn't been
investigated yet** — this benchmark run identifies the gap, it doesn't
explain it, and any explanation offered without profiling would just be a
guess dressed up as an answer. A fixed 4-connection Tier 1 pool absorbing
512 concurrent commands is a plausible place to start looking, since
that's a very different regime from the low-concurrency case ReadUs's
own single-command numbers above look strong in, but that's a hypothesis
to verify, not a conclusion. This is a real, open item — see
[`CONTRIBUTING.md`](../CONTRIBUTING.md) if you want to dig into it.

## ReadUs solo: round-trip latency by payload size

`Ping`/`Get`/`Set` at pipeline depth 1, one connection, `Ping` as
baseline.

| Operation | Payload | Mean | Ratio vs. Ping | Allocated | Alloc ratio vs. Ping |
|---|---:|---:|---:|---:|---:|
| Ping | 16 B | 45.20 us | 1.00 | 368 B | 1.00 |
| Get | 16 B | 45.92 us | 1.02 | 504 B | 1.37 |
| Set | 16 B | 46.89 us | 1.04 | 512 B | 1.39 |
| Ping | 256 B | 45.00 us | 1.00 | 368 B | 1.00 |
| Get | 256 B | 46.28 us | 1.03 | 744 B | 2.02 |
| Set | 256 B | 46.16 us | 1.03 | 512 B | 1.39 |
| Ping | 4096 B | 44.48 us | 1.00 | 368 B | 1.00 |
| Get | 4096 B | 51.35 us | 1.15 | 4584 B | 12.46 |
| Set | 4096 B | 55.55 us | 1.25 | 512 B | 1.39 |

`Set`'s allocation stays flat regardless of payload size — its reply is
always just `+OK`, independent of how much data was written. `Get`'s
allocation scales with the payload, because `RedisResult` copies a bulk
reply's bytes out of the connection's read buffer exactly once, at the
point the value has to leave the read loop (see
[Architecture](guides/architecture.md#the-zero-allocation-goal)) — that
copy is unavoidable and scales with what's actually being read.

## ReadUs solo: pipelined `PING` throughput

Same shape as the comparative pipeline table above, ReadUs only —
included for consistency with the two dedicated ReadUs-only benchmark
classes this repo has had from early on.

| Depth | Mean | Allocated |
|---:|---:|---:|
| 1 | 45.67 us | 568 B |
| 8 | 130.35 us | 3272 B |
| 64 | 583.34 us | 24328 B |
| 512 | 4,090.01 us | 192779 B |

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
