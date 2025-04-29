using System.Threading.Tasks;
using Xunit;

namespace ReadUs.Tests;

[Collection(nameof(RedisClusterFixtureCollection))]
public class RedisClusterConnectionPoolTests
{
    private readonly RedisClusterFixture _redisClusterFixture;

    public RedisClusterConnectionPoolTests(RedisClusterFixture redisClusterFixture)
    {
        _redisClusterFixture = redisClusterFixture;
    }

    [Fact]
    public async Task ItCanGetDatabaseInstance()
    {
        using var clusterConnectionPool = new RedisClusterConnectionPool(
            _redisClusterFixture.ClusterNodes, _redisClusterFixture.Configuration);

        using var redisDatabase = await clusterConnectionPool.GetAsync();

        Assert.IsType<RedisClusterDatabase>(redisDatabase);
    }
}