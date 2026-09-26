namespace ReadUs.Connections;

/// <summary>
/// Thrown for any connection-level failure: connect/TLS/handshake failure, or an I/O
/// fault on an established connection. Distinct from a RESP-level error reply (which
/// arrives as a <see cref="Protocol.RedisResult"/> with <see cref="Protocol.RedisResult.IsError"/>
/// set, not as an exception) and from <see cref="Protocol.RespProtocolException"/> (a
/// framing violation, which is always a symptom of connection corruption and is itself
/// wrapped into one of these before reaching a caller).
/// </summary>
public sealed class RedisConnectionException : Exception
{
    public RedisConnectionException(string message) : base(message)
    {
    }

    public RedisConnectionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
