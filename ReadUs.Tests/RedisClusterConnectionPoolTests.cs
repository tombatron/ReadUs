using System.Threading.Tasks;
using Xunit;

namespace ReadUs.Tests;

[Collection(nameof(RedisClusterFixtureCollection))]
public class RedisClusterConnectionPoolTests(RedisClusterFixture redisClusterFixture)
{
    [Fact]
    public async Task ItCanGetDatabaseInstance()
    {
        using var clusterConnectionPool = new RedisClusterConnectionPool(
            redisClusterFixture.ClusterNodes, redisClusterFixture.Configuration);

        var redisDatabase = await clusterConnectionPool.GetDatabase();
        
        Assert.IsAssignableFrom<IRedisDatabase>(redisDatabase);
    }
}