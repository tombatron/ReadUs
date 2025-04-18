using System;
using System.Linq;
using System.Threading.Tasks;
using ReadUs.ResultModels;
using Xunit;

namespace ReadUs.Tests.Integration;

public sealed class RedisConnectionTests
{
    public sealed class RoleCommandOnSingleInstance(RedisSingleInstanceFixture fixture)
        : IClassFixture<RedisSingleInstanceFixture>
    {
        [Fact]
        public void CanExecute()
        {
            var connection = new RedisConnection(new Uri($"redis://{fixture.SingleNode.GetConnectionString()}"));

            connection.Connect();

            var roleResult = connection.Role().Unwrap();

            Assert.IsType<PrimaryRoleResult>(roleResult);
        }

        [Fact]
        public async Task CanExecuteAsync()
        {
            var connection = new RedisConnection(new Uri($"redis://{fixture.SingleNode.GetConnectionString()}"));

            await connection.ConnectAsync();

            var roleResult = (await connection.RoleAsync()).Unwrap();

            Assert.IsType<PrimaryRoleResult>(roleResult);
        }
    }

    public sealed class RoleCommandOnCluster : IClassFixture<RedisClusterFixture>
    {
        private readonly RedisClusterConnection _connection;

        public RoleCommandOnCluster(RedisClusterFixture fixture)
        {
            var connectionString = new Uri($"redis://{fixture.Node1.GetConnectionString()}?connectionsPerNode=5");

            var connectionPool = new RedisClusterConnectionPool(fixture.ClusterNodes, connectionString);

            var database =
                connectionPool.GetAsync().GetAwaiter().GetResult() as RedisClusterDatabase; // Yeah yeah, I know...

            _connection = database!.Connection as RedisClusterConnection;
        }

        [Fact]
        public void CanExecuteOnPrimaryNode()
        {
            var primaryNode = _connection.First(x => x.Role == ClusterNodeRole.Primary);

            var roleResult = primaryNode.Role().Unwrap();

            Assert.IsType<PrimaryRoleResult>(roleResult);
        }

        [Fact]
        public async Task CanExecuteOnPrimaryNodeAsync()
        {
            var primaryNode = _connection.First(x => x.Role == ClusterNodeRole.Primary);

            var roleResult = (await primaryNode.RoleAsync()).Unwrap();

            Assert.IsType<PrimaryRoleResult>(roleResult);
        }

        [Fact]
        public void CanExecuteOnSecondaryNode()
        {
            var secondaryNode = _connection.First(x => x.Role == ClusterNodeRole.Secondary);

            var roleResult = secondaryNode.Role().Unwrap();

            Assert.IsType<ReplicaRoleResult>(roleResult);
        }

        [Fact]
        public async Task CanExecuteOnSecondaryNodeAsync()
        {
            var secondaryNode = _connection.First(x => x.Role == ClusterNodeRole.Secondary);

            var roleResult = (await secondaryNode.RoleAsync()).Unwrap();

            Assert.IsType<ReplicaRoleResult>(roleResult);
        }
    }
}