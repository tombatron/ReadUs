# Contributing to ReadUs

## Building

Requires the .NET 10 SDK.

```sh
dotnet build
```

The build is expected to produce **zero warnings**. `Directory.Build.props`
enables `EnableNETAnalyzers`/`AnalysisLevel=latest-recommended` across every
project; a PR that introduces a new warning won't be merged as-is. If an
analyzer is flagging a real false positive (it happens — see e.g. the
`CA5350` suppression in `RedisScript.cs`, where SHA1 is mandated by the
Redis `EVALSHA` protocol itself, not a security choice this client makes),
suppress it narrowly with a comment explaining why, rather than disabling
the rule project-wide.

## Testing

```sh
dotnet test
```

- **`ReadUs.Tests.Unit`** — pure, no external dependencies.
- **`ReadUs.Tests.Integration`** — needs a working **Docker** install.
  Every fixture (standalone, TLS, Cluster, Sentinel, and a Toxiproxy pair
  for fault injection) is a disposable Testcontainers-managed container or
  set of containers, spun up fresh per test run and torn down after. This
  project never mocks Redis itself — if a change needs new integration
  coverage, it needs a real server exercising the real behavior, not a
  stand-in. Expect the full integration suite to take on the order of
  10-20 seconds to bootstrap its containers before any test actually runs.
- **`ReadUs.Tests.Fuzz`** and **`ReadUs.Tests.Soak`** are scaffolded
  (`csproj` only) but **not yet implemented** — property-based RESP
  protocol fuzzing and long-running memory/GC soak validation,
  respectively, both named in the original project spec but never
  written. Genuinely open if you want to pick one up.

Integration tests occasionally show timing-sensitive flakes under heavy,
repeated back-to-back full-suite runs (polling loops racing real container
startup contention) — if a single test fails in isolation, that's a real
bug; if it only fails as part of a tight loop of full-suite reruns, retry
before assuming it's new.

## Design-first for non-trivial changes

For anything touching connection lifecycle, pooling, cancellation, or
cluster/sentinel routing, write (or update) the relevant state machine in
[`docs/design/state-machines.md`](docs/design/state-machines.md) before
writing code — this project's own mandated order (see the original spec's
§13) puts "state-machine specs before code" first for a reason: several of
the trickiest bugs found during development (a deadlock in Sentinel's
pub/sub supervisor loop, a completion-claim race in blocking-command
cancellation) were caught specifically because an invariant had been
written down clearly enough to test against.

Smaller changes (a new guide, a benchmark, a straightforward bug fix) don't
need this — use judgment.

## Coding conventions

- `.editorconfig` at the repo root is authoritative and specific — it was
  filled in deliberately (namespace style, expression-bodied members, `var`
  usage, accessibility modifiers, etc.) to match this codebase's actual
  existing conventions, not generic defaults. Run
  `dotnet format style --verify-no-changes` if your IDE isn't picking it
  up.
- No reflection on any command path, including the optional convenience
  packages (`ReadUs.Extensions.Hashes`, `ReadUs.Extensions.Json`) — this is
  a hard bar for this project, not just the core library.
- Don't add speculative configurability or abstractions for a scenario
  that isn't backed by a real, current need. Several fixed constants in
  this codebase (e.g. `RedisConnection.UnblockReconciliationGrace`,
  `ClusterNodeHealthTracker`'s failure threshold) are deliberately not yet
  configurable — that's a documented, intentional choice, not an oversight.

## Pull requests

Keep a PR to one coherent unit of work with tests proving it, matching
this project's own commit-per-phase discipline. Reference the design doc
section your change touches (existing or new) in the PR description.
