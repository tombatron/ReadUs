using ReadUs.Protocol;
using ReadUs.Scripting;

namespace ReadUs.Sentinel;

/// <summary>Thin adapter from <see cref="RedisScript"/>'s executor-delegate shape onto <see cref="SentinelClient"/> — routes to whichever node is currently the known master, exactly like every other <see cref="SentinelClient"/> command.</summary>
public static class SentinelScriptingExtensions
{
    public static ValueTask<RedisResult> EvaluateAsync(this RedisScript script, SentinelClient client, ReadOnlyMemory<byte>[] keys, ReadOnlyMemory<byte>[] args, CancellationToken cancellationToken = default) =>
        script.EvaluateAsync(client.ExecuteAsync, keys, args, cancellationToken);
}
