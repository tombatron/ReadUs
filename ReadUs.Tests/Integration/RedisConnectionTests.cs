using System;
using System.Linq;
using System.Threading.Tasks;
using ReadUs.ResultModels;
using Xunit;

namespace ReadUs.Tests.Integration;

// ReSharper disable once ClassNeverInstantiated.Global
public sealed class RedisConnectionTests
{
    [Collection(nameof(RedisSingleInstanceFixtureCollection))]
    public sealed class RoleCommandOnSingleInstance(RedisSingleInstanceFixture fixture)
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

    [Collection(nameof(RedisClusterFixtureCollection))]
    public sealed class RoleCommandOnCluster
    {
        private readonly RedisClusterConnection _connection;

        public RoleCommandOnCluster(RedisClusterFixture fixture)
        {
            var connectionString = new Uri($"redis://{fixture.ConnectionString}?connectionsPerNode=5");

            var connectionPool = new RedisClusterConnectionPool(fixture.ClusterNodes, connectionString);

            _connection = connectionPool.GetConnection().GetAwaiter().GetResult() as RedisClusterConnection;
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