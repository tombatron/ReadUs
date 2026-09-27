using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ReadUs.Connections;
using ReadUs.Protocol;

namespace ReadUs.Extensions.Json;

/// <summary>
/// Typed POCO helpers over plain <c>GET</c>/<c>SET</c> (project spec §10) — the one
/// item the convenience-layer step (§13 step 7) itself frames as optional/lowest
/// priority, built last and deliberately in its own package: §8 is explicit that
/// "reflection-driven convenience... belongs in an optional, clearly-labeled
/// higher-level convenience package, never in the core command path," and referencing
/// this package (and only it) is what costs anything.
///
/// Every operation has a <see cref="JsonTypeInfo{T}"/> overload — the AOT-safe,
/// reflection-free path §8 asks for wherever reflection-driven convenience is offered
/// at all, typically backed by a caller's own source-generated
/// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> — and a
/// <see cref="JsonSerializerOptions"/> overload (reflection-based, via
/// <c>System.Text.Json</c>'s own runtime reflection, not anything ReadUs does itself)
/// for callers who haven't set one up. Only whole-value <c>GET</c>/<c>SET</c> is
/// covered — hash-field-per-property mapping is a distinctly bigger design task
/// (partial updates, `HGETALL` reply shape) left for a follow-up if this proves
/// worthwhile.
/// </summary>
public static class JsonRedisClientExtensions
{
    public static ValueTask SetJsonAsync<T>(this RedisClient client, ReadOnlyMemory<byte> key, T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default) =>
        SetJsonCoreAsync(client, key, JsonSerializer.SerializeToUtf8Bytes(value, typeInfo), cancellationToken);

    public static ValueTask SetJsonAsync<T>(this RedisClient client, ReadOnlyMemory<byte> key, T value, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default) =>
        SetJsonCoreAsync(client, key, JsonSerializer.SerializeToUtf8Bytes(value, options), cancellationToken);

    public static async ValueTask<T?> GetJsonAsync<T>(this RedisClient client, ReadOnlyMemory<byte> key, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        var reply = await client.ExecuteAsync("GET"u8.ToArray(), [key], cancellationToken).ConfigureAwait(false);
        return TryGetJsonPayload(reply, out var span) ? JsonSerializer.Deserialize(span, typeInfo) : default;
    }

    public static async ValueTask<T?> GetJsonAsync<T>(this RedisClient client, ReadOnlyMemory<byte> key, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        var reply = await client.ExecuteAsync("GET"u8.ToArray(), [key], cancellationToken).ConfigureAwait(false);
        return TryGetJsonPayload(reply, out var span) ? JsonSerializer.Deserialize<T>(span, options) : default;
    }

    private static async ValueTask SetJsonCoreAsync(RedisClient client, ReadOnlyMemory<byte> key, byte[] json, CancellationToken cancellationToken)
    {
        var reply = await client.ExecuteAsync("SET"u8.ToArray(), [key, json], cancellationToken).ConfigureAwait(false);
        if (reply.IsError)
        {
            throw new RedisConnectionException($"SET failed: {reply.AsString()}");
        }
    }

    /// <summary>False (no payload to deserialize) for a missing key; throws for a command-level error; otherwise the raw JSON bytes, undecoded — the same "defer decoding until the caller asks" principle <see cref="RedisResult"/> itself follows.</summary>
    private static bool TryGetJsonPayload(RedisResult reply, out ReadOnlySpan<byte> span)
    {
        if (reply.IsNull)
        {
            span = default;
            return false;
        }

        if (reply.IsError)
        {
            throw new RedisConnectionException($"GET failed: {reply.AsString()}");
        }

        span = reply.AsSpan();
        return true;
    }
}
