using ReadUs.Protocol;

namespace ReadUs.Scripting;

/// <summary>Thin adapter from <see cref="RedisScript"/>'s executor-delegate shape onto <see cref="RedisClient"/>. See <c>ClusterScriptingExtensions</c>/<c>SentinelScriptingExtensions</c> (<c>ReadUs.Cluster</c>/<c>ReadUs.Sentinel</c>) for the other two client types.</summary>
public static class RedisScriptClientExtensions
{
    public static ValueTask<RedisResult> EvaluateAsync(this RedisScript script, RedisClient client, ReadOnlyMemory<byte>[] keys, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default) =>
        script.EvaluateAsync(client.ExecuteAsync, keys, args, cancellationToken);
}
