using ReadUs.ResultModels;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static ReadUs.RedisClusterConnectionPool;
using static ReadUs.Tests.TestUtilities;

namespace ReadUs.Tests.Integration;

public sealed class RedisConnectionTests
{
    public sealed class RoleCommandOnSingleInstance
    {
        [Fact]
        public void CanExecute()
        {
            var connection = new RedisConnection(SingleInstanceConnectionConfigurtion);

            connection.Connect();

            var roleResult = connection.Role();

            Assert.IsType<PrimaryRoleResult>(roleResult);
        }

        [Fact]
        public async Task CanExecuteAsync()
        {
            var connection = new RedisConnection(SingleInstanceConnectionConfigurtion);

            await connection.ConnectAsync();

            var roleResult = await connection.RoleAsync();

            Assert.IsType<PrimaryRoleResult>(roleResult);
        }
    }

    public sealed class RoleCommandOnCluster
    {
        private readonly RedisClusterConnection _connection;

        public RoleCommandOnCluster()
        {

            TryGetClusterInformation(ClusterConnectionConfiguration, out var clusterNodes);

            var connectionPool = new RedisClusterConnectionPool(clusterNodes, ClusterConnectionConfiguration);

            var database = connectionPool.GetAsync().GetAwaiter().GetResult() as RedisClusterDatabase; // Yeah yeah, I know...

            _connection = database.Connection as RedisClusterConnection;
        }

        [Fact]
        public void CanExecuteOnPrimaryNode()
        {
            var primaryNode = _connection.First(x => x.Role == ClusterNodeRole.Primary);

            var roleResult = primaryNode.Role();

            Assert.IsType<PrimaryRoleResult>(roleResult);
        }

        [Fact]
        public async Task CanExecuteOnPrimaryNodeAsync()
        {
            var primaryNode = _connection.First(x => x.Role == ClusterNodeRole.Primary);

            var roleResult = await primaryNode.RoleAsync();

            Assert.IsType<PrimaryRoleResult>(roleResult);
        }

        [Fact]
        public void CanExecuteOnSecondaryNode()
        {
            var secondaryNode = _connection.First(x => x.Role == ClusterNodeRole.Secondary);

            var roleResult = secondaryNode.Role();

            Assert.IsType<ReplicaRoleResult>(roleResult);
        }

        [Fact]
        public async Task CanExecuteOnSecondaryNodeAsync()
        {
            var secondaryNode = _connection.First(x => x.Role == ClusterNodeRole.Secondary);

            var roleResult = await secondaryNode.RoleAsync();

            Assert.IsType<ReplicaRoleResult>(roleResult);
        }
    }
}