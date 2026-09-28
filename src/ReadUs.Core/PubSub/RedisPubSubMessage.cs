using ReadUs.Protocol;

namespace ReadUs.PubSub;

/// <summary>The kind of RESP3 push a <see cref="RedisSubscriber"/> message came in as.</summary>
public enum RedisPubSubMessageKind
{
    /// <summary>An ordinary <c>message</c> push, from a plain <c>SUBSCRIBE</c>d channel.</summary>
    Message,

    /// <summary>A <c>pmessage</c> push, from a <c>PSUBSCRIBE</c>d pattern.</summary>
    PMessage,

    /// <summary>An <c>smessage</c> push (project spec §5/§10), from an <c>SSUBSCRIBE</c>d shard channel.</summary>
    SMessage,
}

/// <summary>
/// One delivered pub/sub message. <see cref="Pattern"/> is only set for
/// <see cref="RedisPubSubMessageKind.PMessage"/> — the pattern that matched, distinct
/// from <see cref="Channel"/>, the concrete channel the publish actually happened on.
/// <see cref="Payload"/> is the raw <see cref="RedisResult"/> rather than a second
/// string/byte type, so callers get the same <c>AsString()</c>/<c>AsSpan()</c>
/// ergonomics as every other reply in this client.
/// </summary>
public readonly record struct RedisPubSubMessage(RedisPubSubMessageKind Kind, string Channel, string? Pattern, RedisResult Payload);
