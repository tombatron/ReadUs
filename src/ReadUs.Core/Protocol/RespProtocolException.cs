namespace ReadUs.Protocol;

/// <summary>
/// Thrown when the byte stream does not conform to RESP framing (unrecognized type
/// prefix, malformed length, missing CRLF terminator, negative length other than the
/// RESP2 null sentinels). This always indicates either a corrupt/desynchronized
/// connection or a server bug — never a normal application-level error (those arrive
/// as <see cref="RespType.Error"/>/<see cref="RespType.BulkError"/> values, not
/// exceptions from the reader).
/// </summary>
public sealed class RespProtocolException(string message) : Exception(message)
{
}
