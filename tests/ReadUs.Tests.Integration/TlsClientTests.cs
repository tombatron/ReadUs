using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using ReadUs.Connections;

namespace ReadUs.Tests.Integration;

/// <summary>
/// Exercises TLS support (project spec §7) against a real TLS-enabled
/// <c>redis-server</c> (port 6400, self-signed certificate — see the session's setup
/// notes) using a custom certificate-validation callback, exactly the shape needed for
/// the private/self-signed CAs common in managed deployments.
/// </summary>
public class TlsClientTests
{
    private static readonly X509Certificate2 ServerCertificate = X509CertificateLoader.LoadCertificateFromFile("/tmp/readus-tls/redis.crt");

    [Fact]
    public async Task ConnectsOverTlsAndValidatesTheServerCertificateViaTheCustomCallback()
    {
        var callbackInvoked = false;

        var options = new RedisConnectionOptions
        {
            EndPoint = new DnsEndPoint("localhost", 6400),
            UseTls = true,
            CertificateValidationCallback = (_, certificate, _, _) =>
            {
                callbackInvoked = true;
                // Real validation, not "always true": confirms the exact self-signed
                // certificate this server was configured with, the way an application
                // would pin a private CA in a managed deployment.
                return certificate is not null && certificate.GetCertHashString() == ServerCertificate.GetCertHashString();
            },
        };

        await using var connection = await RedisConnection.ConnectAsync(options);
        var result = await connection.SendAsync("PING"u8.ToArray(), []);

        Assert.True(callbackInvoked);
        Assert.Equal("PONG", result.AsString());
    }

    [Fact]
    public async Task RejectingTheCertificateFailsTheConnection()
    {
        var options = new RedisConnectionOptions
        {
            EndPoint = new DnsEndPoint("localhost", 6400),
            UseTls = true,
            CertificateValidationCallback = (_, _, _, _) => false,
        };

        await Assert.ThrowsAsync<RedisConnectionException>(async () => await RedisConnection.ConnectAsync(options));
    }

    [Fact]
    public async Task SetAndGetRoundTripOverTls()
    {
        var options = new RedisConnectionOptions
        {
            EndPoint = new DnsEndPoint("localhost", 6400),
            UseTls = true,
            // This test is about the SET/GET round trip actually working over an
            // encrypted channel, not about validation policy (that's the other two
            // tests) — a self-signed cert against a fixed localhost test port.
#pragma warning disable CA5359
            CertificateValidationCallback = (_, _, _, _) => true,
#pragma warning restore CA5359
        };

        await using var connection = await RedisConnection.ConnectAsync(options);
        var key = "readus:test:tls"u8.ToArray();
        var value = "encrypted-in-transit"u8.ToArray();

        await connection.SendAsync("SET"u8.ToArray(), [key, value]);
        var result = await connection.SendAsync("GET"u8.ToArray(), [key]);

        Assert.Equal("encrypted-in-transit", result.AsString());
    }
}
