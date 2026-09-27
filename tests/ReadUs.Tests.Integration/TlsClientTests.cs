using ReadUs.Connections;
using ReadUs.Tests.Integration.Fixtures;

namespace ReadUs.Tests.Integration;

/// <summary>
/// Exercises TLS support (project spec §7) against a real, disposable
/// Testcontainers-managed TLS-enabled <c>redis-server</c> (project spec §9.2,
/// fresh self-signed certificate per run — see <see cref="TlsRedisFixture"/>) using a
/// custom certificate-validation callback, exactly the shape needed for the
/// private/self-signed CAs common in managed deployments.
/// </summary>
[Collection(TlsRedisCollection.Name)]
public class TlsClientTests(TlsRedisFixture fixture)
{
    [Fact]
    public async Task ConnectsOverTlsAndValidatesTheServerCertificateViaTheCustomCallback()
    {
        var callbackInvoked = false;

        var options = new RedisConnectionOptions
        {
            EndPoint = fixture.EndPoint,
            UseTls = true,
            CertificateValidationCallback = (_, certificate, _, _) =>
            {
                callbackInvoked = true;
                // Real validation, not "always true": confirms the exact self-signed
                // certificate this server was configured with, the way an application
                // would pin a private CA in a managed deployment.
                return certificate is not null && certificate.GetCertHashString() == fixture.ServerCertificate.GetCertHashString();
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
            EndPoint = fixture.EndPoint,
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
            EndPoint = fixture.EndPoint,
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
