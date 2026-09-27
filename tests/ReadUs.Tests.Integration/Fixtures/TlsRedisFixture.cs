using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace ReadUs.Tests.Integration.Fixtures;

/// <summary>
/// One disposable TLS-enabled Redis container per test run (project spec §9.2),
/// self-signed certificate generated fresh in-process for every run (no checked-in
/// cert/key files, no dependency on an external <c>openssl</c> binary) via
/// <see cref="CertificateRequest"/> — exports it to PEM and copies it into the
/// container before start with <c>WithResourceMapping</c>. Ordinary bridge networking
/// with a random host port: a standalone TLS server has the same no-announcement
/// requirement as the plain standalone fixture.
/// </summary>
public sealed class TlsRedisFixture : IAsyncLifetime
{
    private const int TlsPort = 6400;

    private IContainer? _container;

    public DnsEndPoint EndPoint { get; private set; } = null!;

    /// <summary>The exact self-signed certificate the server was configured with — for a test's own custom validation callback to compare against, the way an application would pin a private CA.</summary>
    public X509Certificate2 ServerCertificate { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());

        ServerCertificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        var certPem = ServerCertificate.ExportCertificatePem();
        var keyPem = rsa.ExportPkcs8PrivateKeyPem();

        _container = new ContainerBuilder("redis:8.10.1")
            .WithPortBinding(TlsPort, assignRandomHostPort: true)
            .WithResourceMapping(Encoding.ASCII.GetBytes(certPem), "/tls/redis.crt")
            .WithResourceMapping(Encoding.ASCII.GetBytes(keyPem), "/tls/redis.key")
            .WithCommand(
                "redis-server",
                "--port", "0",
                "--tls-port", TlsPort.ToString(CultureInfo.InvariantCulture),
                "--tls-cert-file", "/tls/redis.crt",
                "--tls-key-file", "/tls/redis.key",
                "--tls-ca-cert-file", "/tls/redis.crt",
                "--tls-auth-clients", "no",
                "--save")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(TlsPort))
            .Build();

        await _container.StartAsync().ConfigureAwait(false);

        EndPoint = new DnsEndPoint(_container.Hostname, _container.GetMappedPublicPort(TlsPort));
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }

        ServerCertificate?.Dispose();
    }
}

[CollectionDefinition(Name)]
public sealed class TlsRedisCollection : ICollectionFixture<TlsRedisFixture>
{
    public const string Name = "TlsRedis";
}
