# Managed Redis and TLS

`RedisConnectionOptions` covers what a managed/remote Redis deployment
(AWS ElastiCache/MemoryDB, Azure Cache, GCP Memorystore, Redis Cloud, or
any self-hosted server with TLS enabled) typically needs beyond a
standalone local server.

## TLS

```csharp
using System.Net;
using ReadUs.Connections;

await using var client = await RedisClient.ConnectAsync(new RedisConnectionOptions
{
    EndPoint = new DnsEndPoint("my-cluster.abc123.use1.cache.amazonaws.com", 6379),
    UseTls = true,
});
```

`TlsTargetHost` (SNI name and certificate-validation target) defaults to
the endpoint's own host when it's a `DnsEndPoint`; set it explicitly if
the certificate's name doesn't match the address you're connecting
through. `CertificateValidationCallback` accepts a custom
`RemoteCertificateValidationCallback` — needed for the private/self-signed
CAs common in managed deployments; leave it `null` to use the OS default
trust store. `ClientCertificates` supplies client certificates for mutual
TLS if the server demands one.

## Rotating credentials

Static `Username`/`Password` work for a fixed password, but managed
services increasingly issue short-lived tokens (AWS IAM auth tokens, Azure
Entra ID tokens). `IRedisCredentialsProvider` is the seam:

```csharp
public interface IRedisCredentialsProvider
{
    ValueTask<RedisCredentials> GetCredentialsAsync(CancellationToken cancellationToken);
}
```

Implement it against whatever token source your platform gives you (an
AWS SDK call, an Azure `TokenCredential`, ...) and set
`RedisConnectionOptions.CredentialsProvider` — ReadUs takes no dependency
on any cloud SDK itself; this interface exists precisely so it never
needs to. A `RedisCredentials.ExpiresAt` on the returned credentials drives
a self-rescheduling proactive refresh loop on the connection: it
re-authenticates (an ordinary pipelined `AUTH`, not a reconnect) at 80% of
the token's remaining lifetime. If a refresh itself ever fails, the
connection faults — whichever pool owns it replaces it, and the
replacement re-authenticates from the provider fresh, so "reconnect with
new credentials" falls out of the pool's existing self-healing machinery
for free rather than needing its own separate implementation.

## DNS re-resolution

`RedisConnectionOptions.EndPoint` should be a `DnsEndPoint`, not a
pre-resolved `IPEndPoint`, for any managed deployment where the
underlying IP can change (failover, node replacement). ReadUs never
caches a resolved address for the connection's lifetime — every connect
and reconnect (including a pool's own self-healing replacement of a
faulted connection) re-resolves the hostname fresh.

## See also

- [Getting started](getting-started.md) — the rest of
  `RedisConnectionOptions` (timeouts, keepalive).
- [Architecture](architecture.md) — how a faulted connection gets replaced.
