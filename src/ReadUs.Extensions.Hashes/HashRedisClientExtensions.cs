using ReadUs.Connections;
using ReadUs.Protocol;

namespace ReadUs.Extensions.Hashes;

/// <summary>
/// <c>HSET</c>/<c>HGETALL</c> helpers for a <see cref="RedisHashModelAttribute"/> type
/// (project spec §10) — real per-property hash-field mapping, generated at compile
/// time with zero reflection anywhere in the path (see <see cref="IRedisHashModel{TSelf}"/>).
/// Complements <c>ReadUs.Extensions.Json</c>'s whole-value JSON blob approach rather
/// than replacing it — pick whichever matches how the data actually needs to be
/// queried (individual fields via plain Redis commands vs. one opaque blob).
/// </summary>
public static class HashRedisClientExtensions
{
    public static async ValueTask SetHashAsync<T>(this RedisClient client, ReadOnlyMemory<byte> key, T value, CancellationToken cancellationToken = default)
        where T : IRedisHashModel<T>
    {
        var fields = value.ToHashFields();
        var args = new ReadOnlyMemory<byte>[fields.Count + 1];
        args[0] = key;
        for (var i = 0; i < fields.Count; i++)
        {
            args[i + 1] = fields[i];
        }

        var reply = await client.ExecuteAsync("HSET"u8.ToArray(), args, cancellationToken).ConfigureAwait(false);
        if (reply.IsError)
        {
            throw new RedisConnectionException($"HSET failed: {reply.AsString()}");
        }
    }

    /// <summary>Null if the key doesn't exist (an empty <c>HGETALL</c> reply — a hash with zero fields and a missing key are indistinguishable in Redis, since deleting a hash's last field deletes the key itself).</summary>
    public static async ValueTask<T?> GetHashAsync<T>(this RedisClient client, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
        where T : IRedisHashModel<T>
    {
        var reply = await client.ExecuteAsync("HGETALL"u8.ToArray(), [key], cancellationToken).ConfigureAwait(false);
        if (reply.IsError)
        {
            throw new RedisConnectionException($"HGETALL failed: {reply.AsString()}");
        }

        return reply.AsItems().Length == 0 ? default : T.FromHash(reply);
    }
}
