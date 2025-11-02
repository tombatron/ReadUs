using System.Threading.Tasks;
using Xunit;

namespace ReadUs.Tests;

[Collection(nameof(RedisClusterFixtureCollection))]
public class RedisConnectionPoolClusterTests(RedisClusterFixture redisClusterFixture)
{
    [Fact]
    public async Task ItCanGetDatabaseInstance()
    {
        using var clusterConnectionPool = RedisConnectionPool.Create(redisClusterFixture.ConnectionString);

        var redisDatabase = clusterConnectionPool.GetDatabase();
        
        Assert.IsType<IRedisDatabase>(redisDatabase, exactMatch: false);
    }
}