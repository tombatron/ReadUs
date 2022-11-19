using System.Threading.Tasks;
using Xunit;

namespace ReadUs.Tests
{
    public class RedisClusterConnectionPoolTests : IClassFixture<RedisClusterFixture>
    {
        private readonly RedisClusterFixture _redisClusterFixture;

        public RedisClusterConnectionPoolTests(RedisClusterFixture redisClusterFixture) =>
            _redisClusterFixture = redisClusterFixture;

        [Fact]
        public async Task ItCanGetDatabaseInstance()
        {
            using var clusterConnectionPool = new RedisClusterConnectionPool(
                _redisClusterFixture.ClusterNodes, _redisClusterFixture.Configuration);

            using var redisDatabase = await clusterConnectionPool.GetAsync();

            Assert.IsType<RedisClusterDatabase>(redisDatabase);
        }
    }
}