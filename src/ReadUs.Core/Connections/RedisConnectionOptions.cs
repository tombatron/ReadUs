using System.Net;

namespace ReadUs.Connections;

/// <summary>
/// Connection-level configuration for a single physical connection. TLS and pluggable
/// credential-provider auth (project spec §7) are deferred to the managed-Redis phase;
/// for now this covers what's needed to stand up RESP2/RESP3 against a standalone node
/// (project spec §13 step 2).
/// </summary>
public sealed class RedisConnectionOptions
{
    public required EndPoint EndPoint { get; init; }

    /// <summary>RESP protocol version to negotiate via <c>HELLO</c>. Defaults to RESP3 per project spec §1.</summary>
    public int RespVersion { get; init; } = 3;

    public string? Username { get; init; }

    public string? Password { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
