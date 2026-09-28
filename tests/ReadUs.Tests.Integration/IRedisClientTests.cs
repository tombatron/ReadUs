using System.Net;
using System.Text;
using ReadUs.Cluster;
using ReadUs.Connections;
using ReadUs.Sentinel;
using ReadUs.Tests.Integration.Fixtures;

namespace ReadUs.Tests.Integration;

/// <summary>
/// The actual proof of project spec §10's "same shape for standalone, Cluster, and
/// Sentinel-discovered connections": one piece of code, <see cref="IRedisClientTests.SetAndGetAsync"/>,
/// runs unmodified against a real <see cref="RedisClient"/>, a real <c>ClusterClient</c>,
/// and a real <c>SentinelClient</c>, through the <see cref="IRedisClient"/> type alone —
/// split into three collection-scoped classes (one per fixture type) since a single
/// xUnit test class can only belong to one collection, matching how this suite already
/// splits other cross-cutting concerns.
/// </summary>
public static class IRedisClientTests
{
    public static async Task<string?> SetAndGetAsync(IRedisClient client, string key)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        await client.ExecuteAsync("SET"u8.ToArray(), [keyBytes, "hello"u8.ToArray()]);
        var result = await client.ExecuteAsync("GET"u8.ToArray(), [keyBytes]);
        return result.AsString();
    }
}

[Collection(StandaloneRedisCollection.Name)]
public class IRedisClientTestsStandalone(StandaloneRedisFixture fixture)
{
    [Fact]
    public async Task RedisClientIsUsableThroughIRedisClient()
    {
        await using var client = await RedisClient.ConnectAsync(new RedisConnectionOptions { EndPoint = fixture.EndPoint }, connectionCount: 1);

        var result = await IRedisClientTests.SetAndGetAsync(client, $"readus:test:iredisclient:{Guid.NewGuid():N}");

        Assert.Equal("hello", result);
    }
}

[Collection(ClusterRedisCollection.Name)]
public class IRedisClientTestsCluster(ClusterRedisFixture fixture)
{
    [Fact]
    public async Task ClusterClientIsUsableThroughIRedisClient()
    {
        await using var client = await ClusterClient.ConnectAsync(fixture.SeedEndpoints);

        var result = await IRedisClientTests.SetAndGetAsync(client, $"readus:test:iredisclient:{Guid.NewGuid():N}");

        Assert.Equal("hello", result);
    }
}

[Collection(SentinelRedisCollection.Name)]
public class IRedisClientTestsSentinel(SentinelRedisFixture fixture)
{
    [Fact]
    public async Task SentinelClientIsUsableThroughIRedisClient()
    {
        await using var client = await SentinelClient.ConnectAsync(fixture.SentinelEndpoints, "mymaster");

        var result = await IRedisClientTests.SetAndGetAsync(client, $"readus:test:iredisclient:{Guid.NewGuid():N}");

        Assert.Equal("hello", result);
    }
}
