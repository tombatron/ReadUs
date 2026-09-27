using System.Net;
using Testcontainers.Redis;

namespace ReadUs.Tests.Integration.Fixtures;

/// <summary>
/// One disposable standalone Redis container per test run (project spec §9.2's
/// "spun up via Testcontainers... not mocked"), shared across every test class in the
/// <c>StandaloneRedis</c> collection via <see cref="StandaloneRedisCollection"/> — the
/// container starts once, not once per class or per test. Ordinary bridge networking
/// with a random host port is fine here (unlike Cluster/Sentinel): a standalone server
/// never needs to announce its own address to anything.
/// </summary>
public sealed class StandaloneRedisFixture : IAsyncLifetime
{
    private RedisContainer? _container;

    public DnsEndPoint EndPoint { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new RedisBuilder("redis:8.10.1").Build();
        await _container.StartAsync();
        EndPoint = new DnsEndPoint(_container.Hostname, _container.GetMappedPublicPort(RedisBuilder.RedisPort));
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class StandaloneRedisCollection : ICollectionFixture<StandaloneRedisFixture>
{
    public const string Name = "StandaloneRedis";
}
