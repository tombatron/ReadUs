using ReadUs.Protocol;

namespace ReadUs.Scripting;

/// <summary>
/// Thin adapter from <see cref="RedisScript"/>'s executor-delegate shape onto
/// <see cref="IRedisClient"/> — one adapter for <see cref="RedisClient"/>,
/// <c>ClusterClient</c>, and <c>SentinelClient</c> alike, now that all three implement
/// it. Originally three separate near-identical adapters (one per concrete type),
/// collapsed once <see cref="IRedisClient"/> existed — see
/// docs/design/state-machines.md, "Recorded during implementation of IRedisClient
/// unification."
/// </summary>
public static class RedisScriptClientExtensions
{
    public static ValueTask<RedisResult> EvaluateAsync(this RedisScript script, IRedisClient client, ReadOnlyMemory<byte>[] keys, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default) =>
        script.EvaluateAsync(client.ExecuteAsync, keys, args, cancellationToken);
}
