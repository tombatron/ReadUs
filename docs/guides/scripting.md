# Scripting

`RedisScript` wraps a Lua script body with automatic `EVALSHA`→`EVAL`
fallback — the pattern Redis's own docs recommend to avoid sending the
full script body over the wire on every call, without ever risking a
`NOSCRIPT` error reaching your code.

```csharp
using ReadUs.Scripting;

var script = new RedisScript("return redis.call('SET', KEYS[1], ARGV[1])");

var result = await script.EvaluateAsync(
    client, // any IRedisClient: RedisClient, ClusterClient, or SentinelClient
    keys: ["key"u8.ToArray()],
    args: ["value"u8.ToArray()]);
```

`EvaluateAsync` always tries `EVALSHA` first (`RedisScript` computes the
script's SHA1 itself at construction — no round trip via `SCRIPT LOAD`
needed to learn it). Only on a `NOSCRIPT` error reply does it fall back to
`EVAL` for that one call — which also causes the server to cache the
script under its SHA1, so the *next* call succeeds via `EVALSHA` again.

## No client-side "is it loaded" cache

This is deliberate, not a missed optimization: the server's own script
cache can be evicted independently of this client (`SCRIPT FLUSH`, a
restart), so a client-side "definitely loaded" assumption could go stale
in a way that would silently break every call after that point.
Optimistic-`EVALSHA` + catch-`NOSCRIPT` is the only version of this that
can't go stale — the same approach StackExchange.Redis's own `LuaScript`
takes.

## `FUNCTION`/`FCALL`

No equivalent wrapper exists for Redis Functions, deliberately — unlike a
plain script, a function is explicitly loaded once
(`ReadUs.Generated.ScriptingCommands.FunctionLoadAsync`) and then called
by name (`FcallAsync`/`FcallRoAsync`), both already fully usable through
the ordinary generated command surface (see
[Getting started](getting-started.md#the-generated-typed-command-surface)).
There's no cache-miss/fallback wrinkle to automate the way there is for
`EVAL`/`EVALSHA`.

## Cluster routing

A keyed script call under `ClusterClient` round-robins to an arbitrary
master on its first attempt rather than routing directly by key —
`EVAL`/`EVALSHA`'s key position isn't one the command-table generator
currently resolves statically (see [Architecture](architecture.md) for
what that limit covers generally). A wrong first guess surfaces as an
ordinary `MOVED` reply, which `ClusterClient` follows transparently — the
call still succeeds, just not optimally routed on the very first attempt.

## See also

- [`IRedisClient`](iredisclient.md) — why `EvaluateAsync` can take any of
  the three client types through one interface.
