using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace ReadUs.Connections;

/// <summary>
/// Connection-level configuration for a single physical connection, covering both a
/// standalone node (project spec §13 step 2) and remote/managed Redis (§7): TLS with
/// SNI and custom certificate validation, pluggable rotating-credential auth, and TCP
/// keepalive for the cross-AZ/cross-region blips managed services treat as normal.
/// </summary>
public sealed class RedisConnectionOptions
{
    public required EndPoint EndPoint { get; init; }

    /// <summary>RESP protocol version to negotiate via <c>HELLO</c>. Defaults to RESP3 per project spec §1.</summary>
    public int RespVersion { get; init; } = 3;

    /// <summary>Static password auth. Ignored if <see cref="CredentialsProvider"/> is set.</summary>
    public string? Username { get; init; }

    /// <summary>Static password auth. Ignored if <see cref="CredentialsProvider"/> is set.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// Rotating-credential auth (AWS IAM, Azure Entra ID, ...) — project spec §7. Takes
    /// priority over <see cref="Username"/>/<see cref="Password"/> when set.
    /// </summary>
    public IRedisCredentialsProvider? CredentialsProvider { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Enables TLS (project spec §7) via <see cref="System.Net.Security.SslStream"/>.</summary>
    public bool UseTls { get; init; }

    /// <summary>SNI server name and certificate-validation target host. Defaults to <see cref="EndPoint"/>'s host when it's a <see cref="DnsEndPoint"/>.</summary>
    public string? TlsTargetHost { get; init; }

    /// <summary>Custom server-certificate validation — needed for the private/self-signed CAs common in managed deployments. Null uses the OS default trust store.</summary>
    public RemoteCertificateValidationCallback? CertificateValidationCallback { get; init; }

    /// <summary>Client certificates for mutual TLS, if the provider requires it.</summary>
    public X509CertificateCollection? ClientCertificates { get; init; }

    /// <summary>TCP-level keepalive — cross-AZ/cross-region managed-service blips are the norm, not the exception (project spec §7).</summary>
    public bool EnableTcpKeepAlive { get; init; } = true;

    public TimeSpan TcpKeepAliveTime { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan TcpKeepAliveInterval { get; init; } = TimeSpan.FromSeconds(10);
}
